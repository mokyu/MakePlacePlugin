﻿using Dalamud.Data;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Game.Network;
using Dalamud.IoC;
using Dalamud.Logging;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.Game;
using ImGuiScene;
using Lumina.Excel.GeneratedSheets;
using Lumina.Text;
using MakePlacePlugin.Gui;
using MakePlacePlugin.Objects;
using MakePlacePlugin.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace MakePlacePlugin
{
    public class MakePlacePlugin : IDalamudPlugin
    {
        public string Name => "MakePlace Plugin";
        public PluginUi Gui { get; private set; }
        public Configuration Config { get; private set; }

        [PluginService]
        public static CommandManager CommandManager { get; private set; }
        [PluginService]
        public static Framework Framework { get; private set; }

        [PluginService]
        public static DalamudPluginInterface Interface { get; private set; }
        [PluginService]
        public static GameGui GameGui { get; private set; }
        [PluginService]
        public static ChatGui ChatGui { get; private set; }
        [PluginService]
        public static ClientState ClientState { get; private set; }
        [PluginService]
        public static DataManager Data { get; private set; }

        [PluginService]
        public static SigScanner Scanner { get; private set; }

        [PluginService]
        public static TargetManager TargetMgr { get; private set; }

        [PluginService] public static GameNetwork GameNetwork { get; private set; }


        // Texture dictionary for the housing item icons.
        public readonly Dictionary<ushort, TextureWrap> TextureDictionary = new Dictionary<ushort, TextureWrap>();

        public static List<HousingItem> ItemsToPlace = new List<HousingItem>();


        private delegate bool UpdateLayoutDelegate(IntPtr a1);
        private HookWrapper<UpdateLayoutDelegate> IsSaveLayoutHook;


        // Function for selecting an item, usually used when clicking on one in game.        
        public delegate void SelectItemDelegate(IntPtr housingStruct, IntPtr item);
        private static HookWrapper<SelectItemDelegate> SelectItemHook;



        public static bool ApplyChange = false;

        public static SaveLayoutManager LayoutManager;

        public static bool logHousingDetour = false;

        internal static Location PlotLocation = new Location();

        public void Dispose()
        {
            foreach (var t in this.TextureDictionary)
                t.Value?.Dispose();
            TextureDictionary.Clear();

            HookManager.Dispose();

            Config.PlaceAnywhere = false;
            ClientState.TerritoryChanged -= TerritoryChanged;
            CommandManager.RemoveHandler("/makeplace");
            Gui?.Dispose();

        }

        public MakePlacePlugin(
            [RequiredVersion("1.0")] DalamudPluginInterface pluginInterface,
            [RequiredVersion("1.0")] CommandManager commandManager
        )
        {
            Config = Interface.GetPluginConfig() as Configuration ?? new Configuration();
            Config.Initialize(Interface);
            Config.Save();

            // LoadOffset();
            Initialize();

            CommandManager.AddHandler("/makeplace", new CommandInfo(CommandHandler)
            {
                HelpMessage = "load config window."
            });
            Gui = new PluginUi(this);
            ClientState.TerritoryChanged += TerritoryChanged;


            HousingData.Init(Data, this);
            Memory.Init(Scanner);
            LayoutManager = new SaveLayoutManager(ChatGui, Config);

            PluginLog.Log("MakePlace Plugin v2.1 initialized");
        }
        public void Initialize()
        {

            HookManager.Init(Scanner);

            IsSaveLayoutHook = HookManager.Hook<UpdateLayoutDelegate>("40 53 48 83 ec 20 48 8b d9 48 8b 0d ?? ?? ?? ?? e8 ?? ?? ?? ?? 33 d2 48 8b c8 e8 ?? ?? ?? ?? 84 c0 75 7d 38 83 76 01 00 00", IsSaveLayoutDetour);

            SelectItemHook = HookManager.Hook<SelectItemDelegate>("E8 ?? ?? ?? ?? 48 8B CE E8 ?? ?? ?? ?? 48 8B 6C 24 40 48 8B CE", SelectItemDetour);

            UpdateYardObjHook = HookManager.Hook<UpdateYardDelegate>("48 89 74 24 18 57 48 83 ec 20 b8 dc 02 00 00 0f b7 f2 48 8b f9 66 3b d0 0f", UpdateYardObj);

            GetGameObjectHook = HookManager.Hook<GetObjectDelegate>("48 89 5c 24 08 48 89 74 24 10 57 48 83 ec 20 0f b7 f2 33 db 0f 1f 40 00 0f 1f 84 00 00 00 00 00", GetGameObject);

            GetObjectFromIndexHook = HookManager.Hook<GetActiveObjectDelegate>("81 fa 90 01 00 00 75 08 48 8b 81 88 0c 00 00 c3 0f b7 81 90 0c 00 00 3b d0 72 03 33 c0 c3", GetObjectFromIndex);


        }

        internal delegate IntPtr GetActiveObjectDelegate(IntPtr ObjList, uint index);

        internal IntPtr GetObjectFromIndex(IntPtr ObjList, uint index)
        {

            var result = GetObjectFromIndexHook.Original(ObjList, index);
            return result;
        }

        internal delegate IntPtr GetObjectDelegate(IntPtr ObjList, ushort index);
        internal static HookWrapper<GetObjectDelegate> GetGameObjectHook;
        internal static HookWrapper<GetActiveObjectDelegate> GetObjectFromIndexHook;

        internal IntPtr GetGameObject(IntPtr ObjList, ushort index)
        {
            return GetGameObjectHook.Original(ObjList, index);
        }

        public delegate void UpdateYardDelegate(IntPtr housingStruct, ushort index);
        private static HookWrapper<UpdateYardDelegate> UpdateYardObjHook;

        private void UpdateYardObj(IntPtr objectList, ushort index)
        {
            UpdateYardObjHook.Original(objectList, index);
        }

        unsafe static public void SelectItemDetour(IntPtr housing, IntPtr item)
        {
            SelectItemHook.Original(housing, item);
        }


        unsafe static public void SelectItem(IntPtr item)
        {
            SelectItemDetour((IntPtr)Memory.Instance.HousingStructure, item);
        }


        public static bool IsDecorMode()
        {
            var addon = GameGui.GetAddonByName("HousingGoods", 1);

            return addon != IntPtr.Zero;
        }

        public unsafe static bool IsRotateMode()
        {
            return Memory.Instance.HousingStructure->Mode == HousingLayoutMode.Rotate;
        }

        public unsafe void PlaceNextItem()
        {

            if (!IsDecorMode() || !IsRotateMode() || ItemsToPlace.Count == 0)
            {
                return;
            }

            try
            {

                if (Memory.Instance.IsOutdoors())
                {
                    GetPlotLocation();
                }

                while (ItemsToPlace.Count > 0)
                {
                    var item = ItemsToPlace.First();
                    ItemsToPlace.RemoveAt(0);

                    if (item.ItemStruct == IntPtr.Zero) continue;

                    SetItemPosition(item);

                    if (Config.LoadInterval > 0)
                    {
                        Thread.Sleep(Config.LoadInterval);
                    }

                }



                if (ItemsToPlace.Count == 0)
                {
                    Log("Finished applying layout");
                }

            }
            catch (Exception e)
            {
                LogError($"Error: {e.Message}", e.StackTrace);
            }
        }

        unsafe public static void SetItemPosition(HousingItem rowItem)
        {

            if (!IsDecorMode() || !IsRotateMode())

            {
                LogError("Unable to set position outside of Rotate Layout mode");
                return;
            }

            if (rowItem.ItemStruct == IntPtr.Zero) return;

            Log("Placing " + rowItem.Name);

            var MemInstance = Memory.Instance;

            logHousingDetour = true;
            ApplyChange = true;

            SelectItem(rowItem.ItemStruct);

            Vector3 position = new Vector3(rowItem.X, rowItem.Y, rowItem.Z);
            Vector3 rotation = new Vector3();

            rotation.Y = (float)(rowItem.Rotate * 180 / Math.PI);

            if (MemInstance.IsOutdoors())
            {
                var rotateVector = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -PlotLocation.rotation);
                position = Vector3.Transform(position, rotateVector) + PlotLocation.ToVector();
                rotation.Y = (float)((rowItem.Rotate - PlotLocation.rotation )* 180 / Math.PI);
            }

            MemInstance.WritePosition(position);
            MemInstance.WriteRotation(rotation);
        }

        public void ApplyLayout()
        {
            Log("Applying layout");

            if (Memory.Instance.IsOutdoors())
            {
                ItemsToPlace = new List<HousingItem>(Config.ExteriorItemList);
            }
            else
            {
                ItemsToPlace = new List<HousingItem>(Config.InteriorItemList);
            }

            var thread = new Thread(PlaceNextItem);
            thread.Start();
        }


        public unsafe void MatchLayout()
        {

            List<HousingGameObject> allObjects;
            Memory Mem = Memory.Instance;

            if (Mem.IsIndoors())
            {
                bool dObjectsLoaded = Mem.TryGetNameSortedHousingGameObjectList(out allObjects);
            }
            else
            {
                allObjects = Mem.GetExteriorPlacedObjects();
            }


            List<HousingGameObject> unmatched = new List<HousingGameObject>();

            // first we find perfect match
            foreach (var gameObject in allObjects)
            {

                uint furnitureKey = gameObject.housingRowId;
                var furniture = Data.GetExcelSheet<HousingFurniture>().GetRow(furnitureKey);
                var itemKey = furniture.Item.Value.RowId;

                var houseItem = Config.InteriorItemList.FirstOrDefault(item => item.ItemKey == itemKey && item.Stain == gameObject.color && item.ItemStruct == IntPtr.Zero);
                if (houseItem == null)
                {
                    unmatched.Add(gameObject);
                    continue;
                }

                houseItem.ItemStruct = (IntPtr)gameObject.Item;
            }

            // then we match even if the dye doesn't fit
            foreach (var gameObject in unmatched)
            {

                uint furnitureKey = gameObject.housingRowId;
                var furniture = Data.GetExcelSheet<HousingFurniture>().GetRow(furnitureKey);
                var itemKey = furniture.Item.Value.RowId;

                var houseItem = Config.InteriorItemList.FirstOrDefault(item => item.ItemKey == itemKey && item.ItemStruct == IntPtr.Zero);
                if (houseItem == null)
                {
                    continue;
                }

                houseItem.ItemStruct = (IntPtr)gameObject.Item;
                houseItem.DyeMatch = false;
            }

        }

        public unsafe void GetPlotLocation()
        {
            var mgr = Memory.Instance.HousingModule->outdoorTerritory;
            var territoryId = Memory.Instance.GetTerritoryTypeId();
            var row = Data.GetExcelSheet<TerritoryType>().GetRow(territoryId);

            if (row == null)
            {
                LogError("Cannot identify territory");
                return;
            }

            var placeName = row.PlaceName.Value.Name.ToString();

            PlotLocation = Plots.Map[placeName][mgr->Plot + 1];
        }


        public unsafe void LoadExterior()
        {
            Config.ExteriorItemList.Clear();

            var mgr = Memory.Instance.HousingModule->outdoorTerritory;

            var outdoorMgrAddr = (IntPtr)mgr;
            var objectListAddr = outdoorMgrAddr + 0x10;
            var activeObjList = objectListAddr + 0x8968;

            var exteriorItems = Memory.GetContainer(InventoryType.HousingExteriorPlacedItems);

            GetPlotLocation();
           
            var rotateVector = Quaternion.CreateFromAxisAngle(Vector3.UnitY, PlotLocation.rotation);

            switch (PlotLocation.size)
            {
                case "s":
                    Config.Layout.houseSize = "Small";
                    break;
                case "m":
                    Config.Layout.houseSize = "Medium";
                    break;
                case "l":
                    Config.Layout.houseSize = "Large";
                    break;

            }

            for (int i = 0; i < exteriorItems->Size; i++)
            {
                var item = exteriorItems->GetInventorySlot(i);
                if (item == null || item->ItemID == 0) continue;

                var itemRow = Data.GetExcelSheet<Item>().GetRow(item->ItemID);

                var itemInfo = HousingObjectManager.GetItemInfo(mgr, i);

                var location = new Vector3(itemInfo->X, itemInfo->Y, itemInfo->Z);

                var newLocation = Vector3.Transform(location - PlotLocation.ToVector(), rotateVector);

                var housingItem = new HousingItem(
                    itemRow.RowId,
                    item->Stain,
                    newLocation.X,
                    newLocation.Y,
                    newLocation.Z,
                    itemInfo->Rotation + PlotLocation.rotation,
                    itemRow.Name
                );


                var gameObj = (HousingGameObject*)GetObjectFromIndex(activeObjList, itemInfo->ObjectIndex);

                if (gameObj == null)
                {
                    gameObj = (HousingGameObject*)GetGameObject(objectListAddr, (ushort)(i + 20));
                }

                housingItem.ItemStruct = (IntPtr)gameObj->Item;


                Config.ExteriorItemList.Add(housingItem);
            }

            Config.Save();
        }

        public unsafe void LoadInterior()
        {
            List<HousingGameObject> dObjects;

            SaveLayoutManager.LoadInteriorFixtures();

            Memory.Instance.TryGetNameSortedHousingGameObjectList(out dObjects);

            Config.InteriorItemList.Clear();

            foreach (var gameObject in dObjects)
            {
                //Log($"Processing item #{count++}");

                uint furnitureKey = gameObject.housingRowId;

                var furniture = Data.GetExcelSheet<HousingFurniture>().GetRow(furnitureKey);
                Item item = furniture?.Item?.Value;

                if (item == null) continue;
                if (item.RowId == 0) continue;

                byte stain = gameObject.color;
                var rotate = gameObject.rotation;
                var x = gameObject.X;
                var y = gameObject.Y;
                var z = gameObject.Z;

                var housingItem = new HousingItem(
                        item.RowId,
                        stain,
                        x,
                        y,
                        z,
                        rotate,
                        item.Name
                    );

                housingItem.ItemStruct = (IntPtr)gameObject.Item;

                Config.InteriorItemList.Add(housingItem);
            }

            Config.Save();
        }

        public void LoadLayout()
        {

            Memory Mem = Memory.Instance;

            var itemList = Mem.IsOutdoors() ? Config.ExteriorItemList : Config.InteriorItemList;
            itemList.Clear();

            if (Mem.IsOutdoors())
            {
                LoadExterior();
            }
            else
            {
                LoadInterior();
            }

            Log(String.Format("Loaded {0} furniture items", itemList.Count));

            Config.HiddenScreenItemHistory = new List<int>();
            var territoryTypeId = ClientState.TerritoryType;
            Config.LocationId = territoryTypeId;
            Config.Save();
        }


        public bool IsSaveLayoutDetour(IntPtr housingStruct)
        {
            var result = IsSaveLayoutHook.Original(housingStruct);

            if (ApplyChange)
            {
                ApplyChange = false;
                return true;
            }

            return result;
        }


        private void TerritoryChanged(object sender, ushort e)
        {
            Config.DrawScreen = false;
            Config.Save();
        }

        public unsafe void CommandHandler(string command, string arguments)
        {
            var args = arguments.Trim().Replace("\"", string.Empty);

            try
            {
                if (string.IsNullOrEmpty(args) || args.Equals("config", StringComparison.OrdinalIgnoreCase))
                {
                    Gui.ConfigWindow.Visible = !Gui.ConfigWindow.Visible;
                }
            }
            catch (Exception e)
            {
                LogError(e.Message, e.StackTrace);
            }
        }

        public static void Log(string message, string detail_message = "")
        {
            var msg = $"{message}";
            PluginLog.Log(detail_message == "" ? msg : detail_message);
            ChatGui.Print(msg);
        }
        public static void LogError(string message, string detail_message = "")
        {
            var msg = $"{message}";
            PluginLog.LogError(msg);

            if (detail_message.Length > 0) PluginLog.LogError(detail_message);

            ChatGui.PrintError(msg);
        }

    }

}
