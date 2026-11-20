using HarmonyLib;
using Il2CppFusion.Addons.Physics;
using MelonLoader;

[assembly: MelonInfo(typeof(GuestLite.Mod), "GuestLite", "0.3.0", "letulip")]
[assembly: MelonGame]

namespace GuestLite
{
    // A lighter co-op game for a guest (steps 1, 4 and 5 of docs/guest-netcode.md). A guest's game simulates every car of
    // the session, the wrecks too, and on every snapshot from the host simulates them all again over the ticks it runs
    // ahead (NetDiag 0.4.1, 17 November 2026: 8-16 ticks a snapshot, 2.1-2.4 ms a tick, about 0.1 ms of it a car; in
    // Algeria 10 cars, 7 of them wrecks, the frame 58-76 ms). This mod takes the cars far from this player's out of this
    // game's simulation (Fusion's NetworkRunner.SetIsSimulated, which a client may call in Host mode; Fusion's
    // NetworkRigidbody then makes the body kinematic and draws it from the host's snapshots, as it draws any proxy) and
    // puts each back before this player's car comes near (Far). When a host falls behind and Fusion stops this game's car
    // at its limit of a second ahead, it draws the car on as it went and eases it back once the host's state comes,
    // instead of a car that stands and then jumps (Bridge). And the cars' dangling parts – antennas, mud flaps, whip
    // lights – keep their own swing through Fusion's resimulations instead of swinging in jerks (Dangling). Only on a
    // guest, only this game: nothing goes to the network or the save, the host and the other players see no change, and it
    // can come out at any time.
    public class Mod : MelonMod
    {
        private static MelonPreferences_Category _prefs;
        internal static MelonPreferences_Entry<bool> On, Players, BridgeOn, DanglingOn;
        internal static MelonPreferences_Entry<float> OutBeyond, BackWithin, BridgeMax;

        public override void OnInitializeMelon()
        {
            _prefs = MelonPreferences.CreateCategory("GuestLite");
            On = _prefs.CreateEntry("FarCarsOut", true,
                description: "In someone else's co-op game, the cars far from this player's leave this game's simulation and are drawn from the host's snapshots; false keeps the game's way");
            Players = _prefs.CreateEntry("PlayersCarsToo", true,
                description: "The other players' far cars as well as the wrecks; false takes only the wrecks out");
            OutBeyond = _prefs.CreateEntry("OutBeyond", 150f,
                description: "Metres from this player's car beyond which a car leaves the simulation");
            BackWithin = _prefs.CreateEntry("BackWithin", 120f,
                description: "Metres from this player's car within which a car comes back into the simulation (below OutBeyond, so a car at the edge doesn't flick)");
            BridgeOn = _prefs.CreateEntry("BridgeStalls", true,
                description: "In someone else's co-op game, when the host falls behind and Fusion stops this player's car to wait for it, the car is drawn on as it went (only on this screen) and eased back once the host's state comes; false leaves the car standing, then jumping");
            BridgeMax = _prefs.CreateEntry("BridgeMaxSeconds", 0.5f,
                description: "Seconds the bridge draws the car on at most; past that it holds it where it got to");
            DanglingOn = _prefs.CreateEntry("DanglingParts", true,
                description: "In someone else's co-op game, the cars' dangling parts (antennas, mud flaps, whip lights, shackles, ropes, a pan) swing at their own pace: Fusion's resimulations no longer swing them again; false leaves them swinging in jerks");
            LoggerInstance.Msg("GuestLite loaded");
        }

        public override void OnUpdate()
        {
            Far.Tick();
            Bridge.Update();
            Dangling.AfterTicks();
        }

        public override void OnLateUpdate() => Bridge.LateUpdate();
    }

    // Before each tick's physics step: the dangling parts put back after a resimulation
    [HarmonyPatch(typeof(RunnerSimulatePhysics3D), nameof(RunnerSimulatePhysics3D.SimulatePrimaryScene))]
    internal static class PhysicsStepPatch
    {
        private static void Prefix() => Dangling.BeforeStep();
    }
}
