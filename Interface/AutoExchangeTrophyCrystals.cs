using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Dalamud;
using OmenTools.Info.Game.Data;
using OmenTools.Info.Game.ItemSource;
using OmenTools.Info.Game.ItemSource.Enums;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.Threading;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class AutoExchangeTrophyCrystals : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoExchangeTrophyCrystalsTitle"),
        Description = Lang.Get("AutoExchangeTrophyCrystalsDescription"),
        Category    = ModuleCategory.Script,
        Author      = ["ToxicStar"]
    };

    private Config config = null!;

    private List<ExchangeItem>             availableItems     = [];
    private Dictionary<uint, ExchangeItem> availableItemsByID = [];

    private uint selectedItemID;
    private int  amountInput = 1;

    private bool waitingForPurchase;
    private uint expectedCurrencyAfterPurchase;

    protected override void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new()
        {
            TimeoutMS       = 30_000,
            TimeoutAction   = ResetState,
            ExceptionAction = ResetState
        };

        DService.Instance().AddonLifecycle.RegisterListener
        (
            AddonEvent.PostSetup,
            ["SelectYesno", "ShopExchangeItemDialog", "ShopExchangeCurrencyDialog"],
            OnConfirmAddon
        );
        LoadAvailableItems();
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(OnConfirmAddon);
        if (TaskHelper.IsBusy)
            Abort();
        config.Save(this);
    }

    protected override void ConfigUI()
    {
        if (availableItems.Count == 0)
            LoadAvailableItems();

        ImGuiOm.ConflictKeyText();
        ImGui.Spacing();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{LuminaWrapper.GetItemName(TROPHY_CRYSTAL_ITEM_ID)}: {GetTrophyCrystalCount():N0}");

        if (TaskHelper.IsBusy)
            ImGui.TextUnformatted($"{Lang.Get("Status")}: {TaskHelper.CurrentTaskName}");

        if (availableItems.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(Lang.Get("Loading"));
        }

        ImGui.Spacing();

        using (ImRaii.Disabled(TaskHelper.IsBusy || availableItems.Count == 0))
        {
            DrawItemSelector();
            DrawExchangeList();
        }

        ImGui.Spacing();

        using (ImRaii.Disabled
               (
                   TaskHelper.IsBusy          ||
                   config.Requests.Count == 0 ||
                   availableItems.Count == 0  ||
                   !vnavmeshIPC.IsPluginEnabled()
               ))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Play, Lang.Get("Start")))
                Start();
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(!TaskHelper.IsBusy))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Stop, Lang.Get("Stop")))
                Abort();
        }

        if (!vnavmeshIPC.IsPluginEnabled())
        {
            ImGui.Spacing();
            ImGui.TextColored(KnownColor.OrangeRed.ToVector4(), $"{Lang.Get("PluginPrerequisite")}: vnavmesh");
        }
    }

    private void DrawItemSelector()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{Lang.Get("Add")} {Lang.Get("Item")}:");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(360f * GlobalUIScale);
        var preview = availableItemsByID.TryGetValue(selectedItemID, out var selected) ?
                          $"{selected.ItemName} ({selected.ShopName})" :
                          Lang.Get("PleaseSelect");

        using (var combo = ImRaii.Combo("###TrophyCrystalItem", preview))
        {
            if (combo)
            {
                foreach (var item in availableItems)
                {
                    var isSelected = item.ItemID == selectedItemID;
                    if (ImGui.Selectable($"{item.ItemName} | {item.ShopName} | {item.Cost:N0}###Item_{item.ItemID}", isSelected))
                        selectedItemID = item.ItemID;

                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
            }
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f * GlobalUIScale);
        if (ImGui.InputInt("###TrophyCrystalItemAmount", ref amountInput))
            amountInput = Math.Clamp(amountInput, 1, 999);

        ImGui.SameLine();
        using (ImRaii.Disabled(selectedItemID == 0))
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Plus, Lang.Get("Add")))
            {
                var request = config.Requests.FirstOrDefault(x => x.ItemID == selectedItemID);
                if (request == null)
                    config.Requests.Add(new() { ItemID = selectedItemID, Amount = amountInput });
                else
                    request.Amount = Math.Clamp(request.Amount + amountInput, 1, 999);

                config.Save(this);
            }
        }
    }

    private void DrawExchangeList()
    {
        ImGui.Spacing();

        using var table = ImRaii.Table("TrophyCrystalExchangeList", 3);
        if (!table) return;

        ImGui.TableSetupColumn(Lang.Get("Item"),      ImGuiTableColumnFlags.WidthStretch, 30);
        ImGui.TableSetupColumn(Lang.Get("Amount"),    ImGuiTableColumnFlags.WidthFixed,   90f * GlobalUIScale);
        ImGui.TableSetupColumn(Lang.Get("Operation"), ImGuiTableColumnFlags.None,         10);
        ImGui.TableHeadersRow();

        var currency = GetTrophyCrystalCount();
        foreach (var request in config.Requests.ToList())
        {
            using var id = ImRaii.PushId((int)request.ItemID);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (availableItemsByID.TryGetValue(request.ItemID, out var item))
                ImGui.TextUnformatted($"{item.ItemName} | {item.Cost:N0}");
            else
                ImGui.TextColored(KnownColor.OrangeRed.ToVector4(), $"{Lang.Get("Unknown")} ({request.ItemID})");

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputInt("###Amount", ref request.Amount))
                request.Amount = Math.Clamp(request.Amount, 1, 999);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            ImGui.TableNextColumn();
            var maximum = item is { Cost: > 0 } ?
                              (int)Math.Min(999U, currency / item.Cost) :
                              0;
            using (ImRaii.Disabled(maximum == 0))
            {
                if (ImGui.Button(Lang.Get("Maximum")))
                {
                    request.Amount = maximum;
                    config.Save(this);
                }
            }

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon("Delete", FontAwesomeIcon.TrashAlt, Lang.Get("Delete")))
            {
                config.Requests.Remove(request);
                config.Save(this);
            }
        }
    }

    private void Start()
    {
        if (TaskHelper.IsBusy) return;
        if (DService.Instance().ObjectTable.LocalPlayer == null) return;
        if (DService.Instance().Condition.IsOccupiedInEvent) return;

        if (!vnavmeshIPC.IsPluginEnabled())
        {
            NotifyHelper.Instance().NotificationError($"{Lang.Get("PluginPrerequisite")}: vnavmesh");
            return;
        }

        if (config.Requests.Count == 0 ||
            config.Requests.Any(x => x.Amount is <= 0 or > 999 || !availableItemsByID.ContainsKey(x.ItemID)))
        {
            NotifyHelper.Instance().NotificationError(Lang.Get("AutoExchangeTrophyCrystals-Error-InvalidPreset"));
            return;
        }

        var requests = config.Requests
                             .Select(x => (Item: availableItemsByID[x.ItemID], x.Amount))
                             .ToList();

        var totalCost = requests.Aggregate(0UL, (sum, x) => sum + (ulong)x.Item.Cost * (uint)x.Amount);
        var currency  = GetTrophyCrystalCount();
        if (currency < totalCost)
        {
            NotifyHelper.Instance().NotificationError
            (
                Lang.Get("AutoExchangeTrophyCrystals-Error-InsufficientCurrency", totalCost, currency)
            );
            return;
        }

        ResetState();

        var travelTaskName = $"{Lang.Get("Teleport")} / {Lang.Get("Pathfind")}";

        if (GameState.TerritoryType != TARGET_TERRITORY_ID)
        {
            TaskHelper.Enqueue
            (
                () =>
                {
                    if (AbortOnConflict()) return true;

                    return AetheryteRecordManager.Instance().GetNearestAetheryte
                           (
                               TARGET_TERRITORY_ID,
                               QuartermasterPosition,
                               excludeAethernet: true
                           )?.TeleportTo() == true;
                },
                travelTaskName
            );
        }

        TaskHelper.Enqueue
        (
            () =>
            {
                if (AbortOnConflict()) return true;

                return GameState.TerritoryType == TARGET_TERRITORY_ID && UIModule.IsScreenReady();
            },
            travelTaskName,
            timeoutMS: 120_000
        );
        TaskHelper.Enqueue(StartNavigation, travelTaskName);
        TaskHelper.Enqueue
        (
            () =>
            {
                if (AbortOnConflict()) return true;

                return MoveTo(FindQuartermaster()?.Position ?? QuartermasterPosition);
            },
            travelTaskName
        );

        foreach (var group in requests.GroupBy(x => x.Item.ShopName))
        {
            var shopName     = group.Key;
            var shopTaskName = $"{Lang.Get("Exchange")}: {shopName}";

            TaskHelper.Enqueue(OpenCategoryMenu, shopTaskName);
            TaskHelper.Enqueue(() => SelectShop(shopName), shopTaskName);
            TaskHelper.Enqueue
            (
                () =>
                {
                    if (AbortOnConflict()) return true;

                    return ShopExchangeCurrency->IsAddonAndNodesReady();
                },
                shopTaskName
            );

            foreach (var request in group)
            {
                var purchaseTaskName = $"{Lang.Get("Exchange")}: {request.Item.ItemName} x{request.Amount}";
                TaskHelper.Enqueue(() => BeginPurchase(request.Item, request.Amount), purchaseTaskName);
                TaskHelper.Enqueue(() => WaitForPurchase(request.Item.ItemName), purchaseTaskName);
            }
        }

        TaskHelper.Enqueue(Finish, $"{Lang.Get("Close")} {Lang.Get("Exchange")}");
    }

    private bool StartNavigation()
    {
        if (AbortOnConflict()) return true;

        var targetPosition = FindQuartermaster()?.Position ?? QuartermasterPosition;
        if (LocalPlayerState.DistanceTo3DSquared(targetPosition) <= INTERACT_DISTANCE_SQUARED)
            return true;

        return vnavmeshIPC.PathfindAndMoveToClosely(targetPosition, false, 0.1f);
    }

    private static bool MoveTo
    (
        Vector3 targetPosition
    )
    {
        if (LocalPlayerState.DistanceTo3DSquared(targetPosition) <= INTERACT_DISTANCE_SQUARED)
        {
            vnavmeshIPC.StopPathfind();
            return true;
        }

        var isNavigating = vnavmeshIPC.GetIsPathfindRunning()    ||
                           vnavmeshIPC.GetIsPathfindInProgress() ||
                           vnavmeshIPC.GetIsNavPathfindInProgress();

        if (!isNavigating && Throttler.Shared.Throttle("AutoExchangeTrophyCrystals-RetryPath", 1_000))
            vnavmeshIPC.PathfindAndMoveToClosely(targetPosition, false, 0.1f);

        return false;
    }

    private bool OpenCategoryMenu()
    {
        if (AbortOnConflict()) return true;

        if (SelectIconString->IsAddonAndNodesReady())
        {
            vnavmeshIPC.StopPathfind();
            return true;
        }

        if (ShopExchangeCurrency->IsAddonAndNodesReady())
        {
            // 退出商品列表，重新选择分类。
            ShopExchangeCurrency->Callback(-1);
            return false;
        }

        if (DService.Instance().Condition.IsOccupiedInEvent)
            return false;

        if (FindQuartermaster() is not { } quartermaster) return false;
        if (!MoveTo(quartermaster.Position)) return false;

        if (Throttler.Shared.Throttle("AutoExchangeTrophyCrystals-Interact", 1_000))
            quartermaster.TargetInteract();

        return false;
    }

    private bool SelectShop
    (
        string shopName
    )
    {
        if (AbortOnConflict()) return true;

        return AddonSelectIconStringEvent.Select(shopName);
    }

    private bool BeginPurchase
    (
        ExchangeItem item,
        int          amount
    )
    {
        if (AbortOnConflict()) return true;

        if (FindShopEntry(item.ItemID) is not { } entry) return false;

        if (entry.Cost != item.Cost)
        {
            NotifyHelper.Instance().NotificationError
            (
                Lang.Get("AutoExchangeTrophyCrystals-Error-ExchangeStateChanged", item.ItemName)
            );
            Abort();
            return true;
        }

        var currency = GetTrophyCrystalCount();
        var required = (ulong)entry.Cost * (uint)amount;
        if (required > currency)
        {
            NotifyHelper.Instance().NotificationError
            (
                Lang.Get("AutoExchangeTrophyCrystals-Error-InsufficientCurrency", required, currency)
            );
            Abort();
            return true;
        }

        expectedCurrencyAfterPurchase = currency - (uint)required;
        waitingForPurchase            = true;

        ShopExchangeCurrency->Callback(0, entry.CallbackIndex, amount);
        return true;
    }

    private bool WaitForPurchase
    (
        string itemName
    )
    {
        if (AbortOnConflict()) return true;
        if (!waitingForPurchase) return true;

        var currency = GetTrophyCrystalCount();
        if (IsAnyConfirmationAddonReady())
            return false;

        if (currency == expectedCurrencyAfterPurchase)
        {
            waitingForPurchase = false;
            return true;
        }

        if (currency < expectedCurrencyAfterPurchase)
        {
            NotifyHelper.Instance().NotificationError
            (
                Lang.Get("AutoExchangeTrophyCrystals-Error-ExchangeStateChanged", itemName)
            );
            Abort();
            return true;
        }

        return false;
    }

    private void OnConfirmAddon
    (
        AddonEvent type,
        AddonArgs  args
    )
    {
        if (!TaskHelper.IsBusy || !waitingForPurchase || args.Addon == nint.Zero)
            return;
        if (AbortOnConflict()) return;

        switch (args.AddonName)
        {
            case "SelectYesno":
                // 仅确认包含战利水晶名称的提示。
                AddonSelectYesnoEvent.ClickYes(LuminaWrapper.GetItemName(TROPHY_CRYSTAL_ITEM_ID));
                break;

            case "ShopExchangeItemDialog":
                args.Addon.ToStruct()->Callback(0);
                break;

            case "ShopExchangeCurrencyDialog":
                // 兑换数量确认按钮
                var confirmButton = args.Addon.ToStruct()->GetComponentButtonById(17);
                if (confirmButton != null)
                    confirmButton->Click();
                break;
        }
    }

    private static (uint CallbackIndex, uint Cost)? FindShopEntry
    (
        uint itemID
    )
    {
        var addon = ShopExchangeCurrency;
        if (!addon->IsAddonAndNodesReady() || addon->AtkValuesCount <= SHOP_CALLBACK_INDEX_OFFSET)
            return null;

        // 价格、物品 ID、购买回调序号使用相同的商品下标。
        var itemCount = (int)addon->AtkValues[4].UInt;
        for (var index = 0; index < itemCount; index++)
        {
            if (SHOP_CALLBACK_INDEX_OFFSET + index >= addon->AtkValuesCount) break;
            if (addon->AtkValues[SHOP_ITEM_ID_OFFSET + index].UInt != itemID) continue;

            var callbackIndex = addon->AtkValues[SHOP_CALLBACK_INDEX_OFFSET + index].UInt;
            var cost          = addon->AtkValues[SHOP_COST_OFFSET + index].UInt;
            if (callbackIndex >= (uint)itemCount || cost == 0) return null;

            return (callbackIndex, cost);
        }

        return null;
    }

    private static bool IsAnyConfirmationAddonReady() =>
        SelectYesno->IsAddonAndNodesReady()            ||
        ShopExchangeItemDialog->IsAddonAndNodesReady() ||
        ShopExchangeCurrencyDialog->IsAddonAndNodesReady();

    private bool Finish()
    {
        if (AbortOnConflict()) return true;

        if (ShopExchangeCurrency->IsAddonAndNodesReady())
        {
            ShopExchangeCurrency->Callback(-1);
            return false;
        }

        if (SelectIconString->IsAddonAndNodesReady())
        {
            SelectIconString->Callback(-1);
            return false;
        }

        if (DService.Instance().Condition.IsOccupiedInEvent)
            return false;

        ResetState();
        NotifyHelper.Instance().NotificationSuccess($"{Info.Title}: {Lang.Get("Finished")}");
        return true;
    }

    private bool AbortOnConflict()
    {
        if (!TaskHelper.AbortByConflictKey(this)) return false;

        ResetState();
        return true;
    }

    private void Abort()
    {
        TaskHelper.Abort();
        ResetState();
    }

    private void ResetState()
    {
        vnavmeshIPC.StopPathfind();
        waitingForPurchase = false;
    }

    private void LoadAvailableItems()
    {
        var result = ItemSourceInfo.QueryExchangeItems(TROPHY_CRYSTAL_ITEM_ID);
        if (result is not { State: ItemSourceQueryState.Ready, Data: { } data })
            return;

        List<ExchangeItem> items = [];
        foreach (var item in data.Items)
        {
            foreach (var npc in item.NPCInfos)
            {
                if (npc.ID != QUARTERMASTER_DATA_ID || string.IsNullOrWhiteSpace(npc.ShopName)) continue;

                foreach (var cost in npc.CostInfos)
                {
                    if (cost is not { ItemID: TROPHY_CRYSTAL_ITEM_ID, Cost: > 0 }) continue;

                    items.Add(new(item.ItemID, npc.ShopName, item.GetItemName(), cost.Cost));
                }
            }
        }

        availableItems = items
                         .DistinctBy(x => x.ItemID)
                         .OrderBy(x => x.ShopName)
                         .ThenBy(x => x.ItemName)
                         .ToList();
        availableItemsByID = availableItems.ToDictionary(x => x.ItemID);

        if (!availableItemsByID.ContainsKey(selectedItemID))
            selectedItemID = availableItems.FirstOrDefault()?.ItemID ?? 0;
    }

    private static uint GetTrophyCrystalCount() => LocalPlayerState.GetItemCount(TROPHY_CRYSTAL_ITEM_ID);

    private static IGameObject? FindQuartermaster() =>
        DService.Instance().ObjectTable.FirstOrDefault
        (
            x => x.ObjectKind == ObjectKind.EventNpc && x.DataID == QUARTERMASTER_DATA_ID
        );

    private class Config : ModuleConfig
    {
        public List<ExchangeRequest> Requests = [];
    }

    private sealed class ExchangeRequest
    {
        public uint ItemID;
        public int  Amount = 1;
    }

    private sealed record ExchangeItem
    (
        uint   ItemID,
        string ShopName,
        string ItemName,
        uint   Cost
    );

    #region 常量

    // 狼狱停船场
    private const uint TARGET_TERRITORY_ID = 250;

    // 战利水晶兑换员
    private const uint QUARTERMASTER_DATA_ID = 1038441;

    // 战利水晶
    private const uint TROPHY_CRYSTAL_ITEM_ID = 36656;

    // 商品、价格和购买回调序号的起始下标
    private const int SHOP_ITEM_ID_OFFSET        = 1066;
    private const int SHOP_COST_OFFSET           = 456;
    private const int SHOP_CALLBACK_INDEX_OFFSET = 1310;

    // NPC 交互距离平方（4 × 4）
    private const float INTERACT_DISTANCE_SQUARED = 16f;

    // 战利水晶兑换员备用坐标
    private static readonly Vector3 QuartermasterPosition = new(-4.89825f, 2.05696f, -0.503601f);

    #endregion
}
