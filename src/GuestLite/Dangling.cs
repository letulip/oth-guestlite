using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppFusion;
using MelonLoader;
using UnityEngine;
using NQ = System.Numerics.Quaternion;
using NV = System.Numerics.Vector3;

namespace GuestLite
{
    // The cars' dangling parts (step 5 of docs/guest-netcode.md): antennas, mud flaps, whip lights, shackles, ropes, the
    // pan, the lantern, the toilet seat – each its own Rigidbody on a joint under the car (ConfigurableJoint, HingeJoint or
    // CharacterJoint in the game's prefabs), not networked: every game swings its own.
    //
    // A guest's Fusion simulates the whole scene's physics again over the ticks it runs ahead on every snapshot from the
    // host (RunnerSimulatePhysics3D under SimulateAlways) and puts back only the networked bodies, so the parts get those
    // steps on top: on 17 November 2026 1.7 forward and 2.4-4.4 resimulated ticks a frame, the parts swinging two to three
    // and a half times too fast, in jerks at every snapshot. A host never resimulates: there they swing right.
    //
    // So after Fusion's ticks of every frame each part's pose and motion relative to its car is kept, and when the first
    // forward tick after a resimulation is about to step the physics, each part is put back there relative to where its
    // car is now, to swing on from that. Only the parts of the cars in this game's simulation, and only the parts: the cars,
    // the network and the host see nothing of it.
    internal static class Dangling
    {
        private sealed class Part
        {
            public Rigidbody Body;
            public Transform Transform;
            public NV P, V, W;     // position, velocity and angular velocity relative to the car, in the car's frame
            public NQ R;           // rotation relative to the car
            public bool Kept;
        }

        private sealed class Car
        {
            public NetworkObject Object;
            public Rigidbody Body;
            public string Name;
            public readonly List<Part> Parts = new();
            public float FoundAt;
        }

        private const float Refind = 10f;      // s: a car's parts looked for again (a cosmetic fitted or taken off)
        private const int MaxErrors = 3;

        private static readonly Dictionary<IntPtr, Car> Cars = new();
        private static readonly HashSet<IntPtr> Seen = new();
        private static readonly List<IntPtr> Gone = new();
        internal static NetworkRunner Runner;   // this game's runner while it is a guest (Far sets it every frame)

        private static bool _joined, _off, _resim, _firstTold, _partsTold;
        private static int _errors, _puts, _partsMost, _carsMost;
        private static int _offCount;
        private static float _offSum, _offMax;

        internal static void Joined()
        {
            _joined = true;
            _off = _resim = _firstTold = _partsTold = false;
            _errors = _puts = _partsMost = _carsMost = _offCount = 0;
            _offSum = _offMax = 0f;
            Cars.Clear();
        }

        internal static void Left()
        {
            if (!_joined) return;
            _joined = false;
            Cars.Clear();
            Runner = null;
            if (_partsMost == 0) return;
            MelonLogger.Msg($"GuestLite: the dangling parts in this game: put back after {_puts} resimulation(s), up to {_partsMost} " +
                            $"part(s) on {_carsMost} car(s)" +
                            (_offCount > 0
                                ? $"; the resimulations had carried them {_offSum / _offCount * 100f:0} cm off on average, at most {_offMax * 100f:0} cm"
                                : ""));
        }

        // the cars in this game's simulation, every pass of Far
        internal static void SetCars(List<NetworkObject> simulated)
        {
            if (!_joined || _off) return;
            if (!Mod.DanglingOn.Value)
            {
                Cars.Clear();
                return;
            }
            try
            {
                float now = Time.unscaledTime;
                Seen.Clear();
                foreach (var networked in simulated)
                {
                    if (networked == null) continue;
                    IntPtr key = networked.Pointer;
                    Seen.Add(key);
                    if (!Cars.TryGetValue(key, out var car))
                    {
                        car = new Car { Object = networked, Body = networked.GetComponent<Rigidbody>(), Name = networked.name };
                        Cars[key] = car;
                        Find(car, now);
                    }
                    else if (now - car.FoundAt >= Refind) Find(car, now);
                }
                Gone.Clear();
                foreach (var key in Cars.Keys)
                    if (!Seen.Contains(key)) Gone.Add(key);
                foreach (var key in Gone) Cars.Remove(key);

                int parts = 0, cars = 0;
                foreach (var car in Cars.Values)
                {
                    if (car.Parts.Count == 0) continue;
                    parts += car.Parts.Count;
                    cars++;
                }
                if (parts > _partsMost)
                {
                    _partsMost = parts;
                    _carsMost = cars;
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // after Fusion's ticks of the frame: where each part hangs and how it swings, relative to its car
        internal static void AfterTicks()
        {
            if (!_joined || _off || Cars.Count == 0) return;
            try
            {
                if (_resim)
                {
                    // resimulated this frame with no forward tick after it (Fusion at its limit): put back before it is drawn
                    _resim = false;
                    Put();
                }
                Keep();
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // before each tick's physics step (a Harmony prefix on RunnerSimulatePhysics3D.SimulatePrimaryScene)
        internal static void BeforeStep()
        {
            if (!_joined || _off || Cars.Count == 0) return;
            var runner = Runner;
            if (runner == null) return;
            try
            {
                if (runner.IsResimulation)
                {
                    _resim = true;
                    return;
                }
                if (!_resim) return;
                _resim = false;
                Put();
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // the car's own swinging bodies: a Rigidbody on a joint, active, not kinematic, of no networked object of its own
        private static void Find(Car car, float now)
        {
            car.FoundAt = now;
            car.Parts.Clear();
            if (car.Body == null) return;
            var bodies = car.Object.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; bodies != null && i < bodies.Length; i++)
            {
                var body = bodies[i];
                if (body == null || body.Pointer == car.Body.Pointer) continue;
                if (!body.gameObject.activeInHierarchy || body.isKinematic) continue;
                if (body.GetComponent<Joint>() == null) continue;
                var owner = body.GetComponentInParent<NetworkObject>();
                if (owner == null || owner.Pointer != car.Object.Pointer) continue;
                car.Parts.Add(new Part { Body = body, Transform = body.transform });
            }
            if (_partsTold || car.Parts.Count == 0) return;
            _partsTold = true;
            var names = car.Parts.GroupBy(p => p.Body.name).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key);
            MelonLogger.Msg($"GuestLite: dangling parts on '{car.Name}': {car.Parts.Count} ({string.Join(", ", names)}); they keep their own " +
                            "swing through Fusion's resimulations");
        }

        private static void Keep()
        {
            foreach (var car in Cars.Values)
            {
                if (car.Parts.Count == 0 || car.Body == null) continue;
                NV cp = V(car.Body.position), cv = V(car.Body.velocity), cw = V(car.Body.angularVelocity);
                NQ cr = Q(car.Body.rotation);
                NQ inverse = NQ.Inverse(cr);
                foreach (var part in car.Parts)
                {
                    if (part.Body == null)
                    {
                        part.Kept = false;
                        continue;
                    }
                    NV p = V(part.Body.position);
                    NV arm = p - cp;
                    part.P = NV.Transform(arm, inverse);
                    part.R = NQ.Normalize(inverse * Q(part.Body.rotation));
                    part.V = NV.Transform(V(part.Body.velocity) - (cv + NV.Cross(cw, arm)), inverse);
                    part.W = NV.Transform(V(part.Body.angularVelocity) - cw, inverse);
                    part.Kept = true;
                }
            }
        }

        // each part where it was kept, relative to where its car is now, swinging as it was
        private static void Put()
        {
            int put = 0;
            float off = 0f;
            foreach (var car in Cars.Values)
            {
                if (car.Parts.Count == 0 || car.Body == null) continue;
                NV cp = V(car.Body.position), cv = V(car.Body.velocity), cw = V(car.Body.angularVelocity);
                NQ cr = Q(car.Body.rotation);
                foreach (var part in car.Parts)
                {
                    if (!part.Kept || part.Body == null) continue;
                    NV arm = NV.Transform(part.P, cr);
                    NV p = cp + arm;
                    NQ r = NQ.Normalize(cr * part.R);
                    NV v = cv + NV.Cross(cw, arm) + NV.Transform(part.V, cr);
                    NV w = cw + NV.Transform(part.W, cr);
                    off = Math.Max(off, NV.Distance(V(part.Body.position), p));
                    var up = U(p);
                    var ur = U(r);
                    part.Transform.SetPositionAndRotation(up, ur);
                    part.Body.position = up;
                    part.Body.rotation = ur;
                    part.Body.velocity = U(v);
                    part.Body.angularVelocity = U(w);
                    put++;
                }
            }
            if (put == 0) return;
            _puts++;
            _offCount++;
            _offSum += off;
            _offMax = Math.Max(_offMax, off);
            if (_firstTold) return;
            _firstTold = true;
            MelonLogger.Msg($"GuestLite: first resimulation with the dangling parts put back: {put} part(s); it had carried them up to " +
                            $"{off * 100f:0} cm from where they hung");
        }

        private static void Fail(Exception e)
        {
            if (++_errors >= MaxErrors)
            {
                _off = true;
                Cars.Clear();
                MelonLogger.Warning($"GuestLite: the dangling parts are left to the game for this game after {_errors} errors, the last " +
                                    $"{e.GetType().Name}: {e.Message}");
                return;
            }
            MelonLogger.Warning($"GuestLite: the dangling parts: {e.GetType().Name}: {e.Message}");
        }

        private static NV V(Vector3 v) => new(v.x, v.y, v.z);

        private static NQ Q(Quaternion q)
        {
            var n = new NQ(q.x, q.y, q.z, q.w);
            float length = n.Length();
            return length < 1e-4f || float.IsNaN(length) ? NQ.Identity : n * (1f / length);
        }

        private static Vector3 U(NV v)
        {
            var u = default(Vector3);
            u.x = v.X;
            u.y = v.Y;
            u.z = v.Z;
            return u;
        }

        private static Quaternion U(NQ q)
        {
            var u = default(Quaternion);
            u.x = q.X;
            u.y = q.Y;
            u.z = q.Z;
            u.w = q.W;
            return u;
        }
    }
}
