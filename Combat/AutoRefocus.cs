using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace DailyRoutines.Modules;

public unsafe class AutoRefocus : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoRefocusTitle"),
        Description = GetLoc("AutoRefocusDescription"),
        Category = ModuleCategories.Combat,
    };

    private static readonly CompSig SetFocusTargetByObjectIDSig = new("E8 ?? ?? ?? ?? BA 0C 00 00 00 48 8D 0D");
    private delegate void SetFocusTargetByObjectIDDelegate(TargetSystem* targetSystem, ulong objectID);
    private static Hook<SetFocusTargetByObjectIDDelegate>? SetFocusTargetByObjectIDHook;

    private static ulong FocusTarget;
    private static bool IsNeedToRefocus;

    public override void Init()
    {
        SetFocusTargetByObjectIDHook ??=
            DService.Hook.HookFromSignature<SetFocusTargetByObjectIDDelegate>(
                SetFocusTargetByObjectIDSig.Get(), SetFocusTargetByObjectIDDetour);
        SetFocusTargetByObjectIDHook.Enable();

        if (BoundByDuty) OnZoneChange(DService.ClientState.TerritoryType);
        DService.ClientState.TerritoryChanged += OnZoneChange;
        FrameworkManager.Register(true, OnUpdate);
    }

    private static void OnZoneChange(ushort territory)
    {
        FocusTarget = 0;
        IsNeedToRefocus = PresetSheet.Contents.ContainsKey(territory);
    }

    private static void OnUpdate(IFramework framework)
    {
        if (!Throttler.Throttle("AutoRefocus", 1000)) return;
        if (!IsNeedToRefocus || FocusTarget == 0 || FocusTarget == 0xE000_0000) return;

        if (DService.Targets.FocusTarget == null)
            SetFocusTargetByObjectIDHook.Original(TargetSystem.Instance(), FocusTarget);
    }

    private static void SetFocusTargetByObjectIDDetour(TargetSystem* targetSystem, ulong objectID)
    {
        FocusTarget = objectID;
        SetFocusTargetByObjectIDHook.Original(targetSystem, objectID);
    }

    public override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChange;

        base.Uninit();
    }
}
