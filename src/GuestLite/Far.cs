using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppFusion;
using Il2CppVehiclePhysics;
using MelonLoader;
using UnityEngine;

namespace GuestLite
{
    // Twice a second in someone else's co-op game: every other car of the session (a NetworkObject with a VehicleBase, not
    // this player's: no input authority, not the car on the HUD) farther than OutBeyond from the car this player drives
    // leaves this game's simulation, and comes back once within BackWithin.
    //
    // What Fusion 2.1 does with it (the game's build of 31 October 2026, disassembled): NetworkRunner.SetIsSimulated
    // refuses only in Shared mode (this game runs Host mode); the object's InSimulation flag is cleared and each
    // ISimulationExit behaviour told, and NetworkRigidbody`2's SimulationExit makes its body kinematic; the object then
    // gets no FixedUpdateNetwork and no resimulation, and its NetworkRigidbody draws it between the host's snapshots like
    // any proxy. Back in, the flag is set and each ISimulationEnter behaviour told, but NetworkRigidbody has none: the body
    // would stay kinematic and the car frozen in the prediction, so this mod gives the body back the kinematic state it
    // had (dynamic, for every car measured) and wakes it. Fusion's next resimulation then starts the car from the host's
    // latest state. The flag is set at spawn only otherwise (SimulationBehaviourUpdater.AddObject): a car Fusion puts back
    // on its own (one that left this player's area of interest and came back) is taken out again, and after three such
    // returns left alone.
    //
    // The log: the first pass of a visit in one line (it takes out every far car at once), then each car out and back on a
    // line of its own with its distance and, if its winch is on, the winch's state, and the state of a car a pass after it
    // came back – so that an error of the game's (the winch's NullReferenceException of 17 November 2026) can be set
    // against what this mod did and when.
    internal static class Far
    {
        private const float Every = 0.5f;
        private const float TellEvery = 30f;
        private const int GiveUpAfter = 3;
        private const int SayLines = 40;    // lines a visit for single cars out and back

        private sealed class Car
        {
            public NetworkObject Object;
            public Rigidbody Body;
            public VehicleWinchSystem Winch;
            public string Name;
            public bool Wreck, Out, WasKinematic, GaveUp, CheckNext;
            public int Readded;
        }

        private static readonly Dictionary<uint, Car> Cars = new();
        private static readonly HashSet<uint> Seen = new();
        private static readonly List<uint> Gone = new();
        private static readonly List<NetworkObject> Vehicles = new(), Simulated = new();
        private static float _next, _toldAt, _since, _sweepNearest;
        private static bool _client, _checkTold, _offTold, _firstPassDone, _capTold;
        private static Car _first;
        private static int _errors, _outMost, _takenOut, _wrecksOut, _playersOut, _broughtBack, _readded, _refused, _gaveUp;
        private static int _windowOut, _windowBack, _said, _sweepOut, _sweepWrecks, _sweepLater;

        public static void Tick()
        {
            try
            {
                var runner = OverlandNetworkRunner.Instance?.NetworkRunner;
                bool client = runner != null && runner.IsRunning && runner.IsClient && !runner.IsServer;
                if (client != _client)
                {
                    _client = client;
                    if (client) Joined();
                    else Left(runner);
                }
                Dangling.Runner = client ? runner : null;
                if (!client) return;
                float now = Time.unscaledTime;
                if (now < _next) return;
                _next = now + Every;
                if (runner.LatestServerTick.Raw <= 0) return;   // the host's first state has not come yet
                if (!Mod.On.Value)
                {
                    BringAllBack(runner);
                    var hud = HUDManager.Instance;
                    Bridge.SetCar(Gather(runner, hud != null ? hud.carTransform : null));
                    TellSimulated();
                    return;
                }
                _offTold = false;
                Pass(runner);
                if (now - _toldAt >= TellEvery && _windowOut + _windowBack > 0) Tell(now);
            }
            catch (Exception e)
            {
                _next = Time.unscaledTime + 5f;
                if (_errors++ < 3) MelonLogger.Warning($"GuestLite: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void Pass(NetworkRunner runner)
        {
            var hud = HUDManager.Instance;
            Transform drive = hud != null ? hud.carTransform : null;
            if (drive == null)                                  // no car on the HUD: a menu, a loading screen, on foot
            {
                Bridge.SetCar(null);
                Gather(runner, null);
                TellSimulated();
                return;
            }
            Vehicles.Clear();
            Vector3 here = drive.position;
            NetworkObject driven = null;
            float outBeyond = Math.Max(Mod.OutBeyond.Value, 20f);
            float backWithin = Math.Min(Mod.BackWithin.Value, outBeyond - 10f);
            bool sweep = !_firstPassDone;                      // the first pass takes every far car out: told in one line
            _sweepOut = _sweepWrecks = _sweepLater = 0;
            _sweepNearest = float.MaxValue;

            Seen.Clear();
            var objects = runner.GetAllNetworkObjects();
            for (int i = 0; objects != null && i < objects.Count; i++)
            {
                var networked = objects[i];
                if (networked == null) continue;
                if (networked.GetComponent<VehicleBase>() == null) continue;
                Vehicles.Add(networked);
                var root = networked.transform;
                if (root == drive || drive.IsChildOf(root))     // the car this player rides in
                {
                    if (networked.HasInputAuthority) driven = networked;   // and drives: the bridge's
                    continue;
                }
                if (networked.HasInputAuthority) continue;      // this player's other car
                uint id = networked.Id.Raw;
                Seen.Add(id);
                if (!Cars.TryGetValue(id, out var car) || car.Object == null || car.Object.Pointer != networked.Pointer)
                {
                    string name = networked.name;
                    car = new Car
                    {
                        Object = networked,
                        Body = networked.GetComponent<Rigidbody>(),
                        Winch = networked.GetComponentInChildren<VehicleWinchSystem>(true),
                        Name = name,
                        Wreck = name.StartsWith("Broken_Car_", StringComparison.Ordinal),
                    };
                    Cars[id] = car;
                }
                if (car.GaveUp) continue;
                bool wanted = car.Wreck || Mod.Players.Value;
                Vector3 there = root.position;
                float dx = there.x - here.x, dy = there.y - here.y, dz = there.z - here.z;
                float distance = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                bool simulated = networked.IsInSimulation;

                if (car.CheckNext)
                {
                    car.CheckNext = false;
                    Say($"'{car.Name}' a pass after coming back: in the simulation {(simulated ? "yes" : "no")}, kinematic " +
                        $"{(car.Body == null ? "no body" : car.Body.isKinematic ? "yes" : "no")}, at {distance:0} m");
                }
                if (!car.Out)
                {
                    if (wanted && simulated && distance > outBeyond) TakeOut(runner, car, distance, false, sweep);
                    continue;
                }
                if (!wanted || distance < backWithin)
                {
                    BringBack(runner, car, distance, true);
                    continue;
                }
                if (simulated)
                {
                    // put back by Fusion, not by this mod
                    car.Readded++;
                    _readded++;
                    if (car.Readded >= GiveUpAfter)
                    {
                        car.GaveUp = true;
                        car.Out = false;
                        _gaveUp++;
                        Restore(car);
                        if (_gaveUp <= 5)
                            MelonLogger.Msg($"GuestLite: '{car.Name}' came back into the simulation {car.Readded} times on its own: left there");
                        continue;
                    }
                    TakeOut(runner, car, distance, true, false);
                    continue;
                }
                if (car.Body != null && !car.Body.isKinematic) car.Body.isKinematic = true;
                if (car == _first && !_checkTold)
                {
                    _checkTold = true;
                    MelonLogger.Msg($"GuestLite: '{car.Name}' a pass later: in the simulation {(simulated ? "yes" : "no")}, kinematic " +
                                    $"{(car.Body == null ? "no body" : car.Body.isKinematic ? "yes" : "no")}");
                }
            }

            Bridge.SetCar(driven);
            TellSimulated();

            // the cars gone from the session (a wreck recovered, a player left)
            Gone.Clear();
            foreach (var id in Cars.Keys)
                if (!Seen.Contains(id)) Gone.Add(id);
            foreach (var id in Gone) Cars.Remove(id);

            int outNow = 0;
            foreach (var car in Cars.Values)
                if (car.Out) outNow++;
            _outMost = Math.Max(_outMost, outNow);

            if (!sweep) return;
            _firstPassDone = true;
            MelonLogger.Msg(_sweepOut == 0
                ? $"GuestLite: first pass: none of the {Cars.Count} other cars farther than {outBeyond:0} m"
                : $"GuestLite: first pass: {_sweepOut} of the {Cars.Count} other cars out of this game's simulation ({_sweepWrecks} " +
                  $"wrecks, {_sweepOut - _sweepWrecks} players' cars), the nearest at {_sweepNearest:0} m, " +
                  (_sweepLater == 0 ? "all at once" : $"{_sweepLater} of them on Fusion's next update"));
        }

        private static void TakeOut(NetworkRunner runner, Car car, float distance, bool again, bool sweep)
        {
            if (!again) car.WasKinematic = car.Body != null && car.Body.isKinematic;   // before Fusion makes it kinematic
            if (!runner.SetIsSimulated(car.Object, false))
            {
                car.GaveUp = true;
                if (_refused++ < 3) MelonLogger.Warning($"GuestLite: Fusion would not take '{car.Name}' out of the simulation");
                return;
            }
            bool atOnce = !car.Object.IsInSimulation;
            if (car.Body != null && !car.Body.isKinematic) car.Body.isKinematic = true;
            car.Out = true;
            if (again)
            {
                Say($"'{car.Name}' came back into the simulation on its own ({car.Readded} time(s)): out again at {distance:0} m");
                return;
            }
            _takenOut++;
            _windowOut++;
            if (car.Wreck) _wrecksOut++;
            else _playersOut++;
            if (_first == null) _first = car;
            if (sweep)
            {
                _sweepOut++;
                if (car.Wreck) _sweepWrecks++;
                if (!atOnce) _sweepLater++;
                _sweepNearest = Math.Min(_sweepNearest, distance);
                return;
            }
            Say($"'{car.Name}' out of the simulation at {distance:0} m{(atOnce ? "" : ", on Fusion's next update")}{WinchText(car)}");
        }

        private static void BringBack(NetworkRunner runner, Car car, float distance, bool tell)
        {
            car.Out = false;
            _broughtBack++;
            _windowBack++;
            if (!runner.SetIsSimulated(car.Object, true))
            {
                if (_refused++ < 3) MelonLogger.Warning($"GuestLite: Fusion would not put '{car.Name}' back into the simulation: it stays drawn from the snapshots");
                car.GaveUp = true;
                return;
            }
            Restore(car);
            if (!tell) return;
            car.CheckNext = true;
            Say($"'{car.Name}' back into the simulation at {distance:0} m{WinchText(car)}");
        }

        // the body as it was before this mod took the car out: dynamic for every car measured, and awake for the next tick
        private static void Restore(Car car)
        {
            if (car.Body == null || car.WasKinematic) return;
            car.Body.isKinematic = false;
            car.Body.WakeUp();
        }

        // the car's own winch, when it is on: the state the host last sent (attached to another player's car, to a static
        // anchor or to a movable object)
        private static string WinchText(Car car)
        {
            if (car.Winch == null) return "";
            try
            {
                var state = car.Winch.WinchState;
                return state == VehicleWinchSystem.WinchStates.Disabled ? "" : $", its winch {state}";
            }
            catch (Exception e)
            {
                return $", its winch not read ({e.GetType().Name})";
            }
        }

        // a line for a single car out or back, up to SayLines a visit
        private static void Say(string text)
        {
            if (_said < SayLines)
            {
                _said++;
                MelonLogger.Msg("GuestLite: " + text);
                return;
            }
            if (_capTold) return;
            _capTold = true;
            MelonLogger.Msg($"GuestLite: {SayLines} lines of single cars out and back in this game; the rest only counted");
        }

        // the session's cars without taking any out (FarCarsOut off, or no car on the HUD; Pass does it on its way
        // otherwise): into Vehicles, and the one this player sits in and drives returned
        private static NetworkObject Gather(NetworkRunner runner, Transform drive)
        {
            Vehicles.Clear();
            NetworkObject driven = null;
            var objects = runner.GetAllNetworkObjects();
            for (int i = 0; objects != null && i < objects.Count; i++)
            {
                var networked = objects[i];
                if (networked == null || networked.GetComponent<VehicleBase>() == null) continue;
                Vehicles.Add(networked);
                if (drive == null || !networked.HasInputAuthority) continue;
                var root = networked.transform;
                if (root == drive || drive.IsChildOf(root)) driven = networked;
            }
            return driven;
        }

        // the cars of Vehicles in this game's simulation, to the dangling parts
        private static void TellSimulated()
        {
            Simulated.Clear();
            foreach (var networked in Vehicles)
                if (networked != null && networked.IsInSimulation) Simulated.Add(networked);
            Dangling.SetCars(Simulated);
        }

        private static void BringAllBack(NetworkRunner runner)
        {
            int back = 0;
            foreach (var car in Cars.Values)
            {
                if (!car.Out || car.Object == null) continue;
                BringBack(runner, car, 0f, false);
                back++;
            }
            if (_offTold) return;
            _offTold = true;
            MelonLogger.Msg($"GuestLite: switched off (FarCarsOut = false): {back} car(s) back into the simulation");
        }

        private static void Tell(float now)
        {
            int wrecks = 0, players = 0;
            foreach (var car in Cars.Values)
            {
                if (!car.Out) continue;
                if (car.Wreck) wrecks++;
                else players++;
            }
            MelonLogger.Msg($"GuestLite: out of the simulation now {wrecks} wreck(s) and {players} player's car(s) of the {Cars.Count} " +
                            $"other cars; in the last {now - _toldAt:0} s {_windowOut} taken out, {_windowBack} back");
            _toldAt = now;
            _windowOut = _windowBack = 0;
        }

        private static void Joined()
        {
            Cars.Clear();
            _since = Time.unscaledTime;
            _toldAt = _since;
            _next = 0f;
            _checkTold = _offTold = _firstPassDone = _capTold = false;
            _first = null;
            _outMost = _takenOut = _wrecksOut = _playersOut = _broughtBack = _readded = _refused = _gaveUp = 0;
            _windowOut = _windowBack = _said = 0;
            Bridge.Joined();
            Dangling.Joined();
            MelonLogger.Msg($"GuestLite: in someone else's co-op game: the other cars farther than {Mod.OutBeyond.Value:0} m " +
                            $"({(Mod.Players.Value ? "the wrecks and the players' cars" : "the wrecks only")}) leave this game's " +
                            $"simulation and are drawn from the host's snapshots, back within {Mod.BackWithin.Value:0} m" +
                            (Mod.On.Value ? "" : "; off now (FarCarsOut = false)"));
        }

        private static void Left(NetworkRunner runner)
        {
            Bridge.Left();
            Dangling.Left();
            if (_since <= 0f) return;
            float seconds = Time.unscaledTime - _since;
            _since = 0f;
            // still running but no longer a guest (this game took the session over from a host that left): every car
            // back as the game had it
            if (runner != null && runner.IsRunning)
            {
                foreach (var car in Cars.Values)
                {
                    if (!car.Out || car.Object == null) continue;
                    try
                    {
                        car.Out = false;
                        runner.SetIsSimulated(car.Object, true);
                        Restore(car);
                    }
                    catch (Exception e)
                    {
                        if (_errors++ < 3) MelonLogger.Warning($"GuestLite: '{car.Name}' not put back: {e.Message}");
                    }
                }
            }
            Cars.Clear();
            _first = null;
            var span = TimeSpan.FromSeconds(Math.Max(0f, seconds));
            MelonLogger.Msg($"GuestLite: left the co-op game after {(int)span.TotalMinutes}:{span.Seconds:00}: {_takenOut} car(s) taken " +
                            $"out ({_wrecksOut} wrecks, {_playersOut} players' cars), {_broughtBack} back, at most {_outMost} out at once" +
                            (_readded > 0 ? $"; put back by Fusion on its own {_readded} time(s)" : "") +
                            (_gaveUp > 0 ? $"; {_gaveUp} left in the simulation" : "") +
                            (_refused > 0 ? $"; refused {_refused} time(s)" : ""));
        }
    }
}
