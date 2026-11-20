using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2Cpp;
using Il2CppFusion;
using Il2CppFusion.Addons.Physics;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using UnityEngine.LowLevel;
using NQ = System.Numerics.Quaternion;
using NV = System.Numerics.Vector3;

namespace GuestLite
{
    // The bridge over a host's stall (step 4 of docs/guest-netcode.md): only on this player's screen, only the car this
    // player drives.
    //
    // What happens without it (Fusion 2.1 and the game's build of 31 October 2026, disassembled): a guest simulates its
    // car ahead of the host, but Simulation.CalculateForwardTicks never runs past the host's latest tick plus the tick
    // rate (50 ticks, a second). When the host's state stops coming, this game reaches that limit and runs no tick: the
    // car stands still on the screen. When the state comes, Fusion simulates the missed ticks at once and puts the car
    // where it is now: it jumps. In Algeria on 17 November 2026 the forward ticks fell from 1.67 to 0.85-1.5 a frame in
    // such seconds and the car stepped in 25-46 % of the frames.
    //
    // How the game draws the car: NetworkRigidbody`2.Render draws the interpolated pose plus an offset of its own
    // (_correctionOffsetPosition and _correctionOffsetRotation, ApplyCorrectionSmoothing), which it first multiplies by
    // exp(-deltaTime / CorrectionSmoothingTime); its corrections add to that offset and it fades away. Fusion's
    // default updater simulates before the scripts' Update and draws after it, so an offset written in Update is drawn
    // the same frame, before the camera's LateUpdate. Nothing of it reaches the physics or the network: the game's own
    // corrections use the same offset.
    //
    // So, while Fusion waits at its limit with the car moving, this writes into that offset the way the car went on
    // (its velocity turning with its angular velocity, up to BridgeMaxSeconds, then held), divided by this frame's fade
    // so that what is drawn is exactly that. When Fusion goes on, the car is drawn where it was carried on to and eased
    // to the game's car with a time constant of 0.15 s; the game's own corrections meanwhile are added to that.
    internal static class Bridge
    {
        private enum Mode { Idle, Bridging, Returning }

        private const float MinSpeed = 1.5f;            // m/s: slower, a stall is hardly seen
        private const float ReturnTau = 0.15f;          // s: the eased return's time constant
        private const float TeleportMetres = 15f;       // farther at the resume: a teleport, left to the game
        private const float TeleportDegrees = 90f;
        private const float DoneMetres = 0.01f, DoneDegrees = 0.2f;
        private const float TellEvery = 30f;
        private const int MaxErrors = 3;

        // once a game run: Fusion's place in the frame
        private static bool _orderChecked, _prepared;
        private static string _orderWrong;

        // this visit
        private static bool _joined, _ready, _off, _readyTold, _carTold, _verified, _firstTold;
        private static int _rate = 50, _errors;
        private static float _toldAt;

        // the car this player drives and how the game draws it
        private static NetworkObject _car;
        private static NetworkRigidbodyBase _nrb;
        private static Rigidbody _body;
        private static Transform _drawn;
        private static IntPtr _nrbPtr;
        private static int _offPos = -1, _offRot = -1, _offPending = -1;
        private static string _carName;

        // the bridge's state
        private static Mode _mode;
        private static int _lastTick = int.MinValue;
        private static float _t, _frozen, _gTau;
        private static NV _v, _w, _d, _g0, _r0, _applied, _drawnPos, _drawnPrev;
        private static NQ _g0r, _r0r, _appliedR, _drawnRot;
        private static bool _haveDrawn, _slowStall;
        private static int _checkFrames;
        private static float _checkMoved, _checkWanted;
        private static float _firstSpeed;

        // what it did this visit, and in the last TellEvery
        private static int _episodes, _under01, _under025, _under05, _held, _slowStalls, _teleports, _pendingFrames, _adopted;
        private static float _longest, _residualSum, _residualMax;
        private static int _windowEpisodes;
        private static float _windowLongest, _windowResidualMax;

        // .NET compiles a method the first time it runs: the bridge's first stall on 17 November 2026 cost one frame of
        // 135 ms (GuestLite.OnUpdate 94 ms in one call). Compiled while the game loads into the session, it costs nothing
        // later. Once a game run.
        private static void Prepare()
        {
            if (_prepared) return;
            _prepared = true;
            var watch = Stopwatch.StartNew();
            int done = 0, failed = 0;
            foreach (var type in new[] { typeof(Bridge), typeof(Far), typeof(Dangling) })
            foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
                                                   BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.IsAbstract || method.ContainsGenericParameters) continue;
                try
                {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    done++;
                }
                catch (Exception)
                {
                    failed++;
                }
            }
            MelonLogger.Msg($"GuestLite: its code compiled ahead, {done} methods in {watch.ElapsedMilliseconds} ms" +
                            (failed > 0 ? $" ({failed} left to compile on first use)" : ""));
        }

        internal static void Joined()
        {
            Prepare();
            _joined = true;
            _ready = _off = _readyTold = _carTold = _verified = _firstTold = false;
            _errors = 0;
            _rate = 50;
            _toldAt = Time.unscaledTime;
            Unbind();
            _episodes = _under01 = _under025 = _under05 = _held = _slowStalls = _teleports = _pendingFrames = _adopted = 0;
            _longest = _residualSum = _residualMax = 0f;
            _checkMoved = _checkWanted = 0f;
            _windowEpisodes = 0;
            _windowLongest = _windowResidualMax = 0f;
        }

        internal static void Left()
        {
            if (!_joined) return;
            _joined = false;
            bool told = _ready && !_off;
            Unbind();
            _ready = false;
            if (!told && _episodes == 0) return;
            MelonLogger.Msg($"GuestLite: the bridge in this game: {_episodes} stall(s) drawn over" +
                            (_episodes == 0 ? "" :
                                $" (under 0.1 s {_under01}, 0.1-0.25 s {_under025}, 0.25-0.5 s {_under05}, longer {_held}), the longest " +
                                $"{_longest:0.00} s; the car {(_episodes - _teleports > 0 ? _residualSum / (_episodes - _teleports) : 0f):0.00} m " +
                                $"from the game's on average when Fusion went on, at most {_residualMax:0.00} m") +
                            $"; {_slowStalls} stall(s) of a car under {MinSpeed * 3.6f:0} km/h left alone" +
                            (_teleports > 0 ? $"; {_teleports} too far to ease (left to the game)" : "") +
                            (_adopted > 0 ? $"; the game's corrections taken into the return {_adopted} time(s)" : "") +
                            (_pendingFrames > 0 ? $"; {_pendingFrames} frame(s) the game corrected while drawing" : ""));
        }

        // the car this player sits in and has input authority over (null: none, or someone else's)
        internal static void SetCar(NetworkObject car)
        {
            if (!_joined) return;
            if (car == null)
            {
                if (_car != null) Unbind();
                return;
            }
            if (_car != null && _car.Pointer == car.Pointer) return;
            Unbind();
            try
            {
                var nrb = car.GetComponent<NetworkRigidbodyBase>();
                var body = car.GetComponent<Rigidbody>();
                if (nrb == null || body == null)
                {
                    TellCar($"the bridge left '{car.name}' alone: it has no NetworkRigidbody or no Rigidbody");
                    return;
                }
                IntPtr ptr = nrb.Pointer;
                int pos = Offset(ptr, "_correctionOffsetPosition"), rot = Offset(ptr, "_correctionOffsetRotation");
                int pending = Offset(ptr, "_correctionPending");
                if (pos < 0 || rot < 0 || pending < 0)
                {
                    TellCar($"the bridge left '{car.name}' alone: the game's correction offset is not where it was " +
                            $"(position {pos}, rotation {rot}, pending {pending})");
                    return;
                }
                var target = nrb._interpolationTarget;
                _car = car;
                _nrb = nrb;
                _body = body;
                _nrbPtr = ptr;
                _offPos = pos;
                _offRot = rot;
                _offPending = pending;
                _drawn = target != null ? target : car.transform;
                _carName = car.name;
                _mode = Mode.Idle;
                _haveDrawn = false;
                _lastTick = int.MinValue;
                TellCar($"the bridge draws '{_carName}' by {(target != null ? $"its '{target.name}'" : "its root")} (the game's " +
                        $"correction offset at 0x{pos:x} and 0x{rot:x}, smoothing {nrb.CorrectionSmoothingTime:0.00} s)");
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        internal static void Update()
        {
            if (!_joined || _off) return;
            try
            {
                var runner = OverlandNetworkRunner.Instance?.NetworkRunner;
                if (runner == null || !runner.IsRunning) return;
                if (!_ready && !Ready(runner)) return;
                if (_off) return;
                if (!Mod.BridgeOn.Value)
                {
                    _mode = Mode.Idle;
                    return;
                }
                if (_car == null || _nrb == null || _body == null || _nrbPtr == IntPtr.Zero) return;
                Step(runner);
                float now = Time.unscaledTime;
                if (now - _toldAt >= TellEvery)
                {
                    if (_windowEpisodes > 0)
                        MelonLogger.Msg($"GuestLite: the bridge: {_windowEpisodes} stall(s) in the last {now - _toldAt:0} s drawn over " +
                                        $"(the longest {_windowLongest:0.00} s), the car eased back from at most {_windowResidualMax:0.00} m");
                    _toldAt = now;
                    _windowEpisodes = 0;
                    _windowLongest = _windowResidualMax = 0f;
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // after the game drew the car: where it is on the screen
        internal static void LateUpdate()
        {
            if (!_joined || _off || !_ready || _drawn == null) return;
            try
            {
                _drawnPrev = _drawnPos;
                bool had = _haveDrawn;
                _drawnPos = V(_drawn.position);
                _drawnRot = Q(_drawn.rotation);
                _haveDrawn = true;
                if (_verified || _mode != Mode.Bridging || !had) return;
                // the first bridged frames, the first left out: did the game draw the car on?
                if (++_checkFrames < 2) return;
                _checkMoved += NV.Distance(_drawnPos, _drawnPrev);
                _checkWanted += _v.Length() * Time.deltaTime;
                if (_checkWanted < 0.5f) return;              // summed over the stalls until there is enough to judge
                if (_checkMoved >= 0.5f * _checkWanted && _checkMoved <= 1.6f * _checkWanted)
                {
                    _verified = true;
                    MelonLogger.Msg($"GuestLite: the bridge works: while Fusion waited the car went on {_checkMoved:0.00} m of " +
                                    $"{_checkWanted:0.00} m on the screen");
                    return;
                }
                _off = true;
                _mode = Mode.Idle;
                MelonLogger.Warning(_checkMoved < 0.5f * _checkWanted
                    ? $"GuestLite: the game did not draw the bridge (the car went on {_checkMoved:0.00} m of {_checkWanted:0.00} m " +
                      "while Fusion waited): the bridge is off for this game"
                    : $"GuestLite: the car went on {_checkMoved:0.00} m while Fusion waited, more than the bridge's {_checkWanted:0.00} m: " +
                      "the game carries it on by itself, the bridge is off for this game");
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private static bool Ready(NetworkRunner runner)
        {
            if (runner.LatestServerTick.Raw <= 0) return false;   // the host's first state has not come yet
            try
            {
                int rate = runner.TickRate;
                _rate = rate > 0 ? rate : 50;
            }
            catch (Exception)
            {
                return false;
            }
            if (!_orderChecked)
            {
                _orderChecked = true;
                try
                {
                    _orderWrong = Order();
                }
                catch (Exception e)
                {
                    _orderWrong = $"Unity's frame loop not read ({e.GetType().Name}: {e.Message})";
                }
            }
            _ready = true;
            if (_orderWrong != null) _off = true;
            if (_readyTold) return true;
            _readyTold = true;
            if (_off)
                MelonLogger.Warning($"GuestLite: the bridge over the host's stalls stays off: {_orderWrong}");
            else if (Mod.BridgeOn.Value)
                MelonLogger.Msg($"GuestLite: the bridge over the host's stalls is on: when Fusion stops this game's car at its limit " +
                                $"of {_rate} ticks ahead of the host, the car is drawn on as it went for up to " +
                                $"{Math.Max(0.05f, Mod.BridgeMax.Value):0.0#} s and eased back to the game's car once the host's state comes; " +
                                "only on this screen");
            else
                MelonLogger.Msg("GuestLite: the bridge over the host's stalls is off (BridgeStalls = false)");
            return true;
        }

        private static void Step(NetworkRunner runner)
        {
            int tick = runner.Tick.Raw, latest = runner.LatestServerTick.Raw;
            bool first = _lastTick == int.MinValue;
            bool advanced = tick != _lastTick;
            _lastTick = tick;
            if (first || latest <= 0) return;
            bool atLimit = tick - latest >= _rate - 1;   // Fusion runs no tick past the host's latest plus the tick rate
            float dt = Time.deltaTime;
            float smoothing = _nrb.CorrectionSmoothingTime;
            if (dt <= 0f) return;
            if (smoothing <= 0f)
            {
                if (_mode != Mode.Idle) _mode = Mode.Idle;
                return;                                      // the game draws no offset at all then
            }
            float k = MathF.Exp(-dt / smoothing);            // what the game's render keeps of the offset this frame

            switch (_mode)
            {
                case Mode.Idle:
                    if (advanced)
                    {
                        _slowStall = false;
                        return;
                    }
                    if (!atLimit) return;
                    {
                        var v = V(_body.velocity);
                        if (v.Length() < MinSpeed)
                        {
                            if (!_slowStall) _slowStalls++;
                            _slowStall = true;
                            return;
                        }
                        // the game's own offset fades on with its own time constant
                        Begin(v, ReadV(_offPos), ReadQ(_offRot), smoothing);
                        Bridging(dt, k);
                    }
                    return;

                case Mode.Bridging:
                    if (advanced)
                    {
                        Resume(runner, dt, k);
                        return;
                    }
                    Bridging(dt, k);
                    return;

                case Mode.Returning:
                    if (!advanced && atLimit)
                    {
                        var v = V(_body.velocity);
                        if (v.Length() >= MinSpeed)
                        {
                            // stopped again before it got back: the rest of the return fades on under the new bridge
                            Begin(v, _applied, _appliedR, ReturnTau);
                            Bridging(dt, k);
                            return;
                        }
                    }
                    Returning(dt, k);
                    return;
            }
        }

        private static void Begin(NV v, NV g0, NQ g0r, float gTau)
        {
            _v = v;
            _w = V(_body.angularVelocity);
            _d = NV.Zero;
            _frozen = 0f;
            _g0 = g0;
            _g0r = Valid(g0r);
            _gTau = gTau;
            _mode = Mode.Bridging;
            _checkFrames = 0;
            if (!_firstTold && _episodes == 0) _firstSpeed = v.Length();
        }

        private static void Bridging(float dt, float k)
        {
            float max = Math.Max(0.05f, Mod.BridgeMax.Value);
            float before = _frozen;
            _frozen += dt;
            if (before < max)
            {
                // the part of this frame up to the limit, at its middle: the velocity turning with the car
                float step = Math.Min(dt, max - before);
                _d += Turn(_v, _w, before + step * 0.5f) * step;
            }
            float te = Math.Min(_frozen, max);
            float fg = MathF.Exp(-_frozen / _gTau);
            NV pos = _g0 * fg + _d;
            NQ rot = NQ.Normalize(Spin(_w, te) * Pow(_g0r, fg));
            if (_offPending >= 0 && ReadBool(_offPending)) _pendingFrames++;   // the game will correct in its render first
            Write(pos, rot, k);
        }

        private static void Resume(NetworkRunner runner, float dt, float k)
        {
            float max = Math.Max(0.05f, Mod.BridgeMax.Value);
            float frozen = _frozen;
            bool held = frozen >= max;
            // where the bridge drew the car last frame, carried on over this one
            NV want = _drawnPos;
            NQ wantR = _drawnRot;
            if (!held)
            {
                want += Turn(_v, _w, frozen) * dt;
                wantR = NQ.Normalize(Spin(_w, dt) * wantR);
            }
            // where the game will draw its car this frame without an offset: its body after this frame's ticks, back by
            // the part of a tick its interpolation has not reached
            float alpha = 0.5f;
            try
            {
                alpha = Math.Clamp(runner.LocalAlpha, 0f, 1f);
            }
            catch (Exception)
            {
                // Fusion's alpha not read: half a tick
            }
            NV game = V(_body.position) - V(_body.velocity) * ((1f - alpha) / _rate);
            NQ gameR = Q(_body.rotation);
            NV r = want - game;
            NQ rr = NQ.Normalize(wantR * NQ.Inverse(gameR));
            float off = r.Length();

            _episodes++;
            _windowEpisodes++;
            if (frozen < 0.1f) _under01++;
            else if (frozen < 0.25f) _under025++;
            else if (frozen < 0.5f) _under05++;
            else _held++;
            _longest = Math.Max(_longest, frozen);
            _windowLongest = Math.Max(_windowLongest, frozen);

            if (!_haveDrawn || off > TeleportMetres || Degrees(rr) > TeleportDegrees)
            {
                _teleports++;
                _mode = Mode.Idle;
                WriteV(_offPos, NV.Zero);                     // no bridge drawn over a teleport
                WriteQ(_offRot, NQ.Identity);
                return;
            }
            _residualSum += off;
            _residualMax = Math.Max(_residualMax, off);
            _windowResidualMax = Math.Max(_windowResidualMax, off);
            if (!_firstTold)
            {
                _firstTold = true;
                MelonLogger.Msg($"GuestLite: first bridge: Fusion waited {frozen:0.00} s for the host with '{_carName}' at " +
                                $"{_firstSpeed * 3.6f:0} km/h; drawn on {_d.Length():0.0} m, {off:0.00} m from the game's car when " +
                                "Fusion went on, eased back");
            }
            _r0 = r;
            _r0r = rr;
            _t = 0f;
            _mode = Mode.Returning;
            Write(r, rr, k);
        }

        private static void Returning(float dt, float k)
        {
            // the game's own corrections of this frame's ticks were added to what it drew last frame: take them in
            NV now = ReadV(_offPos);
            float kept = MathF.Exp(-_t / ReturnTau);
            if (NV.Distance(now, _applied) > 0.001f && now.LengthSquared() > 1e-8f)
            {
                _r0 += (now - _applied) / kept;
                _adopted++;
            }
            _t += dt;
            kept = MathF.Exp(-_t / ReturnTau);
            NV r = _r0 * kept;
            NQ rr = Pow(_r0r, kept);
            if (r.Length() < DoneMetres && Degrees(rr) < DoneDegrees)
            {
                _mode = Mode.Idle;                            // what is left fades with the game's own smoothing
                return;
            }
            Write(r, rr, k);
        }

        // into the game's offset, so that after its render's fade (k) exactly pos and rot are drawn
        private static void Write(NV pos, NQ rot, float k)
        {
            WriteV(_offPos, pos / k);
            WriteQ(_offRot, Pow(rot, 1f / k));
            _applied = pos;
            _appliedR = rot;
        }

        private static void Unbind()
        {
            _car = null;
            _nrb = null;
            _body = null;
            _drawn = null;
            _nrbPtr = IntPtr.Zero;
            _offPos = _offRot = _offPending = -1;
            _mode = Mode.Idle;
            _haveDrawn = false;
            _lastTick = int.MinValue;
        }

        private static void TellCar(string text)
        {
            if (_carTold) return;
            _carTold = true;
            MelonLogger.Msg("GuestLite: " + text);
        }

        private static void Fail(Exception e)
        {
            if (++_errors >= MaxErrors)
            {
                _off = true;
                _mode = Mode.Idle;
                MelonLogger.Warning($"GuestLite: the bridge is off for this game after {_errors} errors, the last {e.GetType().Name}: {e.Message}");
                return;
            }
            MelonLogger.Warning($"GuestLite: the bridge: {e.GetType().Name}: {e.Message}");
        }

        // null when Fusion simulates before the scripts' Update and draws after it (its default updater); else why not
        private static string Order()
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var phases = root.subSystemList;
            for (int p = 0; phases != null && p < phases.Length; p++)
            {
                var phase = phases[p];
                if (Name(phase.type) != "Update") continue;
                var subs = phase.subSystemList;
                int scripts = -1, simulate = -1, render = -1;
                for (int i = 0; subs != null && i < subs.Length; i++)
                {
                    string name = Name(subs[i].type);
                    if (name == "ScriptRunBehaviourUpdate") scripts = i;
                    else if (name.StartsWith("NetworkRunner", StringComparison.Ordinal))
                    {
                        if (name.Contains("Render")) render = i;
                        else if (name.Contains("Update")) simulate = i;
                    }
                }
                if (scripts < 0 || simulate < 0 || render < 0)
                    return $"Fusion's place in Unity's frame not found (the scripts' Update {scripts}, Fusion's simulation {simulate}, its render {render})";
                if (simulate > scripts || render < scripts)
                    return $"Fusion simulates at {simulate} and draws at {render} of Unity's Update, the scripts at {scripts}: an offset written there would come a frame late";
                return null;
            }
            return "Unity's frame loop has no Update phase";
        }

        private static string Name(Il2CppSystem.Type type)
        {
            try
            {
                return type != null ? type.Name : "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        // the offset of an instance field, by its name, in the object's class or the classes it derives from
        private static int Offset(IntPtr obj, string name)
        {
            for (IntPtr klass = IL2CPP.il2cpp_object_get_class(obj); klass != IntPtr.Zero; klass = IL2CPP.il2cpp_class_get_parent(klass))
            {
                IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(klass, name);
                if (field != IntPtr.Zero) return (int)IL2CPP.il2cpp_field_get_offset(field);
            }
            return -1;
        }

        private static unsafe NV ReadV(int offset)
        {
            float* p = (float*)((byte*)_nrbPtr + offset);
            return new NV(p[0], p[1], p[2]);
        }

        private static unsafe NQ ReadQ(int offset)
        {
            float* p = (float*)((byte*)_nrbPtr + offset);
            return new NQ(p[0], p[1], p[2], p[3]);
        }

        private static unsafe bool ReadBool(int offset) => *((byte*)_nrbPtr + offset) != 0;

        private static unsafe void WriteV(int offset, NV v)
        {
            float* p = (float*)((byte*)_nrbPtr + offset);
            p[0] = v.X;
            p[1] = v.Y;
            p[2] = v.Z;
        }

        private static unsafe void WriteQ(int offset, NQ q)
        {
            float* p = (float*)((byte*)_nrbPtr + offset);
            p[0] = q.X;
            p[1] = q.Y;
            p[2] = q.Z;
            p[3] = q.W;
        }

        private static NV V(Vector3 v) => new(v.x, v.y, v.z);

        private static NQ Q(Quaternion q) => Valid(new NQ(q.x, q.y, q.z, q.w));

        private static NQ Valid(NQ q)
        {
            float n = q.Length();
            return n < 1e-4f || float.IsNaN(n) ? NQ.Identity : q * (1f / n);
        }

        // the rotation the angular velocity w (rad/s, world) turns in t seconds
        private static NQ Spin(NV w, float t)
        {
            float rate = w.Length();
            float angle = rate * t;
            return angle < 1e-6f ? NQ.Identity : NQ.CreateFromAxisAngle(w / rate, angle);
        }

        private static NV Turn(NV v, NV w, float t) => NV.Transform(v, Spin(w, t));

        // q to the power s: the same axis, s times the angle (under half a turn)
        private static NQ Pow(NQ q, float s)
        {
            q = Valid(q);
            if (q.W < 0f) q = NQ.Negate(q);
            float half = MathF.Acos(Math.Clamp(q.W, -1f, 1f));
            float sin = MathF.Sin(half);
            if (sin < 1e-6f) return NQ.Identity;
            var axis = new NV(q.X, q.Y, q.Z) / sin;
            float angle = Math.Min(2f * half * s, MathF.PI * 0.99f);
            return NQ.CreateFromAxisAngle(axis, angle);
        }

        private static float Degrees(NQ q)
        {
            q = Valid(q);
            return 2f * MathF.Acos(Math.Clamp(Math.Abs(q.W), 0f, 1f)) * (180f / MathF.PI);
        }
    }
}
