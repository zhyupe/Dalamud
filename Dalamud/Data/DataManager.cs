using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http.Json;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Interface.Internal;
using Dalamud.IoC;
using Dalamud.IoC.Internal;
using Dalamud.Networking.Http;
using Dalamud.Utility;
using Dalamud.Utility.Timing;
using ImGuiScene;
using JetBrains.Annotations;
using Lumina;
using Lumina.Data;
using Lumina.Data.Files;
using Lumina.Excel;
using Lumina.Excel.GeneratedSheets;
using Lumina.Text;
using Newtonsoft.Json;
using Serilog;

namespace Dalamud.Data;

/// <summary>
/// This class provides data for Dalamud-internal features, but can also be used by plugins if needed.
/// </summary>
[PluginInterface]
[InterfaceVersion("1.0")]
[ServiceManager.BlockingEarlyLoadedService]
public sealed class DataManager : IDisposable, IServiceType
{
    private const string IconFileFormat = "ui/icon/{0:D3}000/{1}{2:D6}.tex";

    private readonly Thread luminaResourceThread;
    private readonly CancellationTokenSource luminaCancellationTokenSource;
    private readonly FFXIVOpcodes ffxivOpcodes;

    [ServiceManager.ServiceConstructor]
    private DataManager(DalamudStartInfo dalamudStartInfo, Dalamud dalamud)
    {
        this.Language = dalamudStartInfo.Language;

        // Set up default values so plugins do not null-reference when data is being loaded.
        this.ClientOpCodes = this.ServerOpCodes = new ReadOnlyDictionary<string, ushort>(new Dictionary<string, ushort>());

        var baseDir = dalamud.AssetDirectory.FullName;
        try
        {
            Log.Verbose("Starting data load...");

            var zoneOpCodeDict = JsonConvert.DeserializeObject<Dictionary<string, ushort>>(
                File.ReadAllText(Path.Combine(baseDir, "UIRes", "serveropcode.json")))!;
            this.ServerOpCodes = new ReadOnlyDictionary<string, ushort>(zoneOpCodeDict);

            Log.Verbose("Loaded {0} ServerOpCodes.", zoneOpCodeDict.Count);

            var clientOpCodeDict = JsonConvert.DeserializeObject<Dictionary<string, ushort>>(
                File.ReadAllText(Path.Combine(baseDir, "UIRes", "clientopcode.json")))!;
            this.ClientOpCodes = new ReadOnlyDictionary<string, ushort>(clientOpCodeDict);

            Log.Verbose("Loaded {0} ClientOpCodes.", clientOpCodeDict.Count);

            using (Timings.Start("Lumina Init"))
            {
                var luminaOptions = new LuminaOptions
                {
                    LoadMultithreaded = true,
                    CacheFileResources = true,
#if NEVER // Lumina bug
                    PanicOnSheetChecksumMismatch = true,
#else
                    PanicOnSheetChecksumMismatch = false,
#endif
                    DefaultExcelLanguage = this.Language.ToLumina(),
                };

                var processModule = Process.GetCurrentProcess().MainModule;
                if (processModule != null)
                {
                    this.GameData = new GameData(Path.Combine(Path.GetDirectoryName(processModule.FileName)!, "sqpack"), luminaOptions);
                }
                else
                {
                    throw new Exception("Could not main module.");
                }

                Log.Information("Lumina is ready: {0}", this.GameData.DataPath);

                try
                {
                    var tsInfo =
                        JsonConvert.DeserializeObject<LauncherTroubleshootingInfo>(
                            dalamudStartInfo.TroubleshootingPackData);
                    this.HasModifiedGameDataFiles =
                        tsInfo?.IndexIntegrity is LauncherTroubleshootingInfo.IndexIntegrityResult.Failed or LauncherTroubleshootingInfo.IndexIntegrityResult.Exception;
                }
                catch
                {
                    // ignored
                }
            }

            this.IsDataReady = true;

            this.luminaCancellationTokenSource = new();

            var luminaCancellationToken = this.luminaCancellationTokenSource.Token;
            this.luminaResourceThread = new(() =>
            {
                while (!luminaCancellationToken.IsCancellationRequested)
                {
                    if (this.GameData.FileHandleManager.HasPendingFileLoads)
                    {
                        this.GameData.ProcessFileHandleQueue();
                    }
                    else
                    {
                        Thread.Sleep(5);
                    }
                }
            });
            this.luminaResourceThread.Start();
            this.ChangeWorldForCN();

            this.ffxivOpcodes = new FFXIVOpcodes("CN");
            this.LoadFFXIVOpcodes();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not download data.");
        }
    }

    private void LoadFFXIVOpcodes()
    {
        var serverMap = new[]
        {
            // Dalamud, FFXIVOpcodes
            ("ActorControlSelf", "ActorControlSelf"),
            ("ContainerInfo", "ContainerInfo"),
            ("MarketBoardItemRequestStart", "MarketBoardItemListingCount"),
            ("MarketBoardHistory", "MarketBoardItemListingHistory"),
            ("MarketBoardOfferings", "MarketBoardItemListing"),
            ("MarketBoardPurchase", "MarketBoardPurchase"),
            ("InventoryActionAck", "InventoryActionAck"),
            ("MarketTaxRates", "ResultDialog"),
            ("RetainerInformation", "RetainerInformation"),
            ("ItemMarketBoardInfo", "ItemMarketBoardInfo"),
            ("CfNotifyPop", "CFNotify"),
        };

        var clientMap = new[]
        {
            // Dalamud, FFXIVOpcodes
            ("MarketBoardPurchaseHandler", "MarketBoardPurchaseHandler"),
        };

        var update = (ReadOnlyDictionary<string, ushort> original, (string, string)[] map) =>
        {
            var dict = new Dictionary<string, ushort>(original);
            foreach (var (dalamudKey, opcodeKey) in map)
            {
                if (this.ffxivOpcodes.ServerOpCodes.TryGetValue(opcodeKey, out var opcode))
                {
                    Log.Verbose("Setting {0} to {1} per FFXIVOpcodes.", dalamudKey, opcode);
                    if (!dict.TryAdd(dalamudKey, opcode))
                    {
                        dict[dalamudKey] = opcode;
                    }
                }
            }

            return new ReadOnlyDictionary<string, ushort>(dict);
        };

        Task.Run(async () =>
        {
            await this.ffxivOpcodes.Init();
            this.ServerOpCodes = update(this.ServerOpCodes, serverMap);
            this.ClientOpCodes = update(this.ClientOpCodes, clientMap);
        });
    }

    /// <summary>
    /// 为国服服务器临时修正isPublic & DataCenter数据.
    /// </summary>
    private void ChangeWorldForCN()
    {
        var chineseWorldDCGroups = new[] {
                new
                {
                    Name = "陆行鸟",
                    Id   = 101u,
                    Worlds = new[]
                    {
                        new { Id = 1175u, Name = "晨曦王座" },
                        new { Id = 1174u, Name = "沃仙曦染" },
                        new { Id = 1173u, Name = "宇宙和音" },
                        new { Id = 1167u, Name = "红玉海"   },
                        new { Id = 1060u, Name = "萌芽池"   },
                        new { Id = 1081u, Name = "神意之地" },
                        new { Id = 1044u, Name = "幻影群岛" },
                        new { Id = 1042u, Name = "拉诺西亚" },
                    },
                },
                new
                {
                   Name = "莫古力",
                   Id   = 102u,
                   Worlds = new[]
                   {
                        new { Id = 1121u, Name = "拂晓之间" },
                        new { Id = 1166u, Name = "龙巢神殿" },
                        new { Id = 1113u, Name = "旅人栈桥" },
                        new { Id = 1076u, Name = "白金幻象" },
                        new { Id = 1176u, Name = "梦羽宝境" },
                        new { Id = 1171u, Name = "神拳痕"   },
                        new { Id = 1170u, Name = "潮风亭"   },
                        new { Id = 1172u, Name = "白银乡"   },
                   },
                },
                new
                {
                   Name = "猫小胖",
                   Id   = 103u,
                   Worlds = new[]
                   {
                        new { Id = 1179u, Name = "琥珀原"   },
                        new { Id = 1178u, Name = "柔风海湾" },
                        new { Id = 1177u, Name = "海猫茶屋" },
                        new { Id = 1169u, Name = "延夏"    },
                        new { Id = 1106u, Name = "静语庄园" },
                        new { Id = 1045u, Name = "摩杜纳"   },
                        new { Id = 1043u, Name = "紫水栈桥" },
                   },
                },
                new
                {
                   Name = "豆豆柴",
                   Id   = 201u,
                   Worlds = new[]
                   {
                        new { Id = 1201u, Name = "红茶川"    },
                        new { Id = 1186u, Name = "伊修加德"  },
                        new { Id = 1180u, Name = "太阳海岸"  },
                        new { Id = 1183u, Name = "银泪湖"    },
                        new { Id = 1192u, Name = "水晶塔"    },
                        new { Id = 1202u, Name = "萨雷安"    },
                        new { Id = 1203u, Name = "加雷马"    },
                        new { Id = 1200u, Name = "亚马乌罗提" },
                   },
                },
            };
        var dcExcel = this.GameData.Excel.GetSheet<WorldDCGroupType>();
        var worldExcel = this.GameData.Excel.GetSheet<World>();
        foreach (var dc in chineseWorldDCGroups)
        {
            var dcToReplaced = dcExcel.GetRow(dc.Id);
            dcToReplaced.Name = new SeString(dc.Name);
            dcToReplaced.Region = 5;

            foreach (var world in dc.Worlds)
            {
                var worldToUpdated = worldExcel.GetRow(world.Id);
                worldToUpdated.IsPublic = true;
                worldToUpdated.DataCenter = new LazyRow<WorldDCGroupType>(this.GameData, dc.Id, Lumina.Data.Language.ChineseSimplified);
            }
        }

    }

    /// <summary>
    /// Gets the current game client language.
    /// </summary>
    public ClientLanguage Language { get; private set; }

    /// <summary>
    /// Gets the OpCodes sent by the server to the client.
    /// </summary>
    public ReadOnlyDictionary<string, ushort> ServerOpCodes { get; private set; }

    /// <summary>
    /// Gets the OpCodes sent by the client to the server.
    /// </summary>
    [UsedImplicitly]
    public ReadOnlyDictionary<string, ushort> ClientOpCodes { get; private set; }

    /// <summary>
    /// Gets a <see cref="Lumina"/> object which gives access to any excel/game data.
    /// </summary>
    public GameData GameData { get; private set; }

    /// <summary>
    /// Gets an <see cref="ExcelModule"/> object which gives access to any of the game's sheet data.
    /// </summary>
    public ExcelModule Excel => this.GameData.Excel;

    /// <summary>
    /// Gets a value indicating whether Game Data is ready to be read.
    /// </summary>
    public bool IsDataReady { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the game data files have been modified by another third-party tool.
    /// </summary>
    public bool HasModifiedGameDataFiles { get; private set; }

    #region Lumina Wrappers

    /// <summary>
    /// Get an <see cref="ExcelSheet{T}"/> with the given Excel sheet row type.
    /// </summary>
    /// <typeparam name="T">The excel sheet type to get.</typeparam>
    /// <returns>The <see cref="ExcelSheet{T}"/>, giving access to game rows.</returns>
    public ExcelSheet<T>? GetExcelSheet<T>() where T : ExcelRow
    {
        return this.Excel.GetSheet<T>();
    }

    /// <summary>
    /// Get an <see cref="ExcelSheet{T}"/> with the given Excel sheet row type with a specified language.
    /// </summary>
    /// <param name="language">Language of the sheet to get.</param>
    /// <typeparam name="T">The excel sheet type to get.</typeparam>
    /// <returns>The <see cref="ExcelSheet{T}"/>, giving access to game rows.</returns>
    public ExcelSheet<T>? GetExcelSheet<T>(ClientLanguage language) where T : ExcelRow
    {
        return this.Excel.GetSheet<T>(ClientLanguage.ChineseSimplified.ToLumina());
    }

    /// <summary>
    /// Get a <see cref="FileResource"/> with the given path.
    /// </summary>
    /// <param name="path">The path inside of the game files.</param>
    /// <returns>The <see cref="FileResource"/> of the file.</returns>
    public FileResource? GetFile(string path)
    {
        return this.GetFile<FileResource>(path);
    }

    /// <summary>
    /// Get a <see cref="FileResource"/> with the given path, of the given type.
    /// </summary>
    /// <typeparam name="T">The type of resource.</typeparam>
    /// <param name="path">The path inside of the game files.</param>
    /// <returns>The <see cref="FileResource"/> of the file.</returns>
    public T? GetFile<T>(string path) where T : FileResource
    {
        var filePath = GameData.ParseFilePath(path);
        if (filePath == null)
            return default;
        return this.GameData.Repositories.TryGetValue(filePath.Repository, out var repository) ? repository.GetFile<T>(filePath.Category, filePath) : default;
    }

    /// <summary>
    /// Check if the file with the given path exists within the game's index files.
    /// </summary>
    /// <param name="path">The path inside of the game files.</param>
    /// <returns>True if the file exists.</returns>
    public bool FileExists(string path)
    {
        return this.GameData.FileExists(path);
    }

    /// <summary>
    /// Get a <see cref="TexFile"/> containing the icon with the given ID.
    /// </summary>
    /// <param name="iconId">The icon ID.</param>
    /// <returns>The <see cref="TexFile"/> containing the icon.</returns>
    public TexFile? GetIcon(uint iconId)
    {
        return this.GetIcon(this.Language, iconId);
    }

    /// <summary>
    /// Get a <see cref="TexFile"/> containing the icon with the given ID, of the given quality.
    /// </summary>
    /// <param name="isHq">A value indicating whether the icon should be HQ.</param>
    /// <param name="iconId">The icon ID.</param>
    /// <returns>The <see cref="TexFile"/> containing the icon.</returns>
    public TexFile? GetIcon(bool isHq, uint iconId)
    {
        var type = isHq ? "hq/" : string.Empty;
        return this.GetIcon(type, iconId);
    }

    /// <summary>
    /// Get a <see cref="TexFile"/> containing the icon with the given ID, of the given language.
    /// </summary>
    /// <param name="iconLanguage">The requested language.</param>
    /// <param name="iconId">The icon ID.</param>
    /// <returns>The <see cref="TexFile"/> containing the icon.</returns>
    public TexFile? GetIcon(ClientLanguage iconLanguage, uint iconId)
    {
        var type = iconLanguage switch
        {
            ClientLanguage.Japanese => "ja/",
            ClientLanguage.English => "en/",
            ClientLanguage.German => "de/",
            ClientLanguage.French => "fr/",
            ClientLanguage.ChineseSimplified => "chs/",
            _ => throw new ArgumentOutOfRangeException(nameof(iconLanguage), $"Unknown Language: {iconLanguage}"),
        };

        return this.GetIcon(type, iconId);
    }

    /// <summary>
    /// Get a <see cref="TexFile"/> containing the icon with the given ID, of the given type.
    /// </summary>
    /// <param name="type">The type of the icon (e.g. 'hq' to get the HQ variant of an item icon).</param>
    /// <param name="iconId">The icon ID.</param>
    /// <returns>The <see cref="TexFile"/> containing the icon.</returns>
    public TexFile? GetIcon(string? type, uint iconId)
    {
        type ??= string.Empty;
        if (type.Length > 0 && !type.EndsWith("/"))
            type += "/";

        var filePath = string.Format(IconFileFormat, iconId / 1000, type, iconId);
        var file = this.GetFile<TexFile>(filePath);

        if (type == string.Empty || file != default)
            return file;

        // Couldn't get specific type, try for generic version.
        filePath = string.Format(IconFileFormat, iconId / 1000, string.Empty, iconId);
        file = this.GetFile<TexFile>(filePath);
        return file;
    }

    /// <summary>
    /// Get a <see cref="TexFile"/> containing the HQ icon with the given ID.
    /// </summary>
    /// <param name="iconId">The icon ID.</param>
    /// <returns>The <see cref="TexFile"/> containing the icon.</returns>
    public TexFile? GetHqIcon(uint iconId)
        => this.GetIcon(true, iconId);

    /// <summary>
    /// Get the passed <see cref="TexFile"/> as a drawable ImGui TextureWrap.
    /// </summary>
    /// <param name="tex">The Lumina <see cref="TexFile"/>.</param>
    /// <returns>A <see cref="TextureWrap"/> that can be used to draw the texture.</returns>
    public TextureWrap? GetImGuiTexture(TexFile? tex)
    {
        return tex == null ? null : Service<InterfaceManager>.Get().LoadImageRaw(tex.GetRgbaImageData(), tex.Header.Width, tex.Header.Height, 4);
    }

    /// <summary>
    /// Get the passed texture path as a drawable ImGui TextureWrap.
    /// </summary>
    /// <param name="path">The internal path to the texture.</param>
    /// <returns>A <see cref="TextureWrap"/> that can be used to draw the texture.</returns>
    public TextureWrap? GetImGuiTexture(string path)
        => this.GetImGuiTexture(this.GetFile<TexFile>(path));

    /// <summary>
    /// Get a <see cref="TextureWrap"/> containing the icon with the given ID.
    /// </summary>
    /// <param name="iconId">The icon ID.</param>
    /// <returns>The <see cref="TextureWrap"/> containing the icon.</returns>
    public TextureWrap? GetImGuiTextureIcon(uint iconId)
        => this.GetImGuiTexture(this.GetIcon(iconId));

    /// <summary>
    /// Get a <see cref="TextureWrap"/> containing the icon with the given ID, of the given quality.
    /// </summary>
    /// <param name="isHq">A value indicating whether the icon should be HQ.</param>
    /// <param name="iconId">The icon ID.</param>
    /// <returns>The <see cref="TextureWrap"/> containing the icon.</returns>
    public TextureWrap? GetImGuiTextureIcon(bool isHq, uint iconId)
        => this.GetImGuiTexture(this.GetIcon(isHq, iconId));

    /// <summary>
    /// Get a <see cref="TextureWrap"/> containing the icon with the given ID, of the given language.
    /// </summary>
    /// <param name="iconLanguage">The requested language.</param>
    /// <param name="iconId">The icon ID.</param>
    /// <returns>The <see cref="TextureWrap"/> containing the icon.</returns>
    public TextureWrap? GetImGuiTextureIcon(ClientLanguage iconLanguage, uint iconId)
        => this.GetImGuiTexture(this.GetIcon(iconLanguage, iconId));

    /// <summary>
    /// Get a <see cref="TextureWrap"/> containing the icon with the given ID, of the given type.
    /// </summary>
    /// <param name="type">The type of the icon (e.g. 'hq' to get the HQ variant of an item icon).</param>
    /// <param name="iconId">The icon ID.</param>
    /// <returns>The <see cref="TextureWrap"/> containing the icon.</returns>
    public TextureWrap? GetImGuiTextureIcon(string type, uint iconId)
        => this.GetImGuiTexture(this.GetIcon(type, iconId));

    /// <summary>
    /// Get a <see cref="TextureWrap"/> containing the HQ icon with the given ID.
    /// </summary>
    /// <param name="iconId">The icon ID.</param>
    /// <returns>The <see cref="TextureWrap"/> containing the icon.</returns>
    public TextureWrap? GetImGuiTextureHqIcon(uint iconId)
        => this.GetImGuiTexture(this.GetHqIcon(iconId));

    #endregion

    /// <summary>
    /// Dispose this DataManager.
    /// </summary>
    void IDisposable.Dispose()
    {
        this.luminaCancellationTokenSource.Cancel();
    }

    private class LauncherTroubleshootingInfo
    {
        public enum IndexIntegrityResult
        {
            Failed,
            Exception,
            NoGame,
            ReferenceNotFound,
            ReferenceFetchFailure,
            Success,
        }

        public IndexIntegrityResult IndexIntegrity { get; set; }
    }
}
