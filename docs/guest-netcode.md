# A guest's lag in co-op: what a client-only mod can change

Research of 16–17 November 2026, read-only: the game's code (build of 31 October 2026, disassembled with names; the
interop of 13 November), Photon's Fusion 2 documentation and the OTH-LAN mod's notes (ideas only, no code taken).
The measurements behind it are NetDiag 0.4.0's, the owner's sessions of 16 November (`~/Downloads/Latest (31).log`,
`(32).log`).

## What the measurements say

A guest is thrown about when the host falls behind, whatever the ping: in Asgard (125 ms of ping) the guest ran a
whole second ahead of the host, at Fusion's limit, its input reached the host every 0.23–0.29 s, and its car stepped in
92–100 % of the frames; in Grimly Ridge (515 ms) the guest ran 0.35–0.44 s ahead and its car never stepped. In Algeria
(300 ms) each new snapshot made the guest simulate ~24 ticks again in one frame, ~50 ms, three to five times a second,
and the car stepped in 10–21 % of the frames, corrections up to 9 m.

## Step 0, measured (17 November 2026)

NetDiag 0.4.1, `~/Downloads/Latest (33).log`: alone as the host in Algeria, Asgard and Grimly Ridge, then a guest in
someone else's session on each.

- As the host all smooth: 29.2–29.7 fps, 14–32 frames over 50 ms in 1.5–2 minutes, no car steps.
- As a guest: Grimly Ridge (ping 256 ms, 21 ticks ahead) 25.2 fps, 1 % low 13.3, 468 frames over 50 ms in 1:53;
  Asgard (175 ms, 15 ticks) 27.8 fps, 29 frames over 50 ms in 2:06; Algeria (255 ms, 24 ticks) 25.5 fps, 1 % low 13.3,
  600 frames over 50 ms in 2:19. The hosts kept up or were shaky (0.45–0.75 s ahead); none fell behind.
- The car hardly stepped: 0.2–0.7 % of the frames. The game's corrections, 157–514 a visit, are nearly all extrapolated
  (tick gap 0–6) and small (under 0.5 m for nine in ten); 3–10 a visit crossed the car's snap limits. The ones not
  extrapolated are the wrecks at rest, a few millimetres. Lever 1 has 3–10 jumps in two minutes to win while the host
  keeps up.
- What hurts a guest is the resimulation's cost: each snapshot simulates 8–16 ticks again (at most 19–37) in one frame,
  26–47 ms of Fusion, the frame 58–76 ms. A tick costs 2.1–2.4 ms: the physics 0.9–1.3 ms, the scripts (every car's
  Vehicle Physics Pro) 1.2–1.4 ms. The lower the ping, the fewer ticks: Asgard at 175 ms had a twentieth of the long
  frames.
- The input delay holds at its minimum, 5 ticks (100 ms): max(100 ms, round trip − 10 ticks).

## Step 1, measured (17 November 2026)

GuestLite 0.1.0 with NetDiag 0.4.1, `~/Downloads/Latest (34).log`: a guest in five sessions of others – Valley (431 ms of
ping), Grimly Ridge (176 ms), Algeria twice (143 and 262 ms) and Asgard (269 ms), 1:37 to 6:53 each.

- Every car farther than 150 m left the guest's simulation at once (Fusion's `SetIsSimulated` refused none) and was
  kinematic a pass later; NetDiag's census showed the far wrecks and players' cars not simulated, kinematic and asleep.
  The players' cars that came within 120 m (four times, in Algeria and Asgard) were simulated and dynamic at the next
  census. No car came back on its own; GuestLite cost 0.0 ms a frame (one pass at most 9 ms). No wreck came within
  120 m: a wreck put back for towing is not checked yet.
- A resimulated tick fell from 2.2–2.5 ms to 0.9–1.3 ms: the scripts from 1.25–1.59 to 0.41–0.57 ms a tick (about
  0.13 ms for each car taken out), the physics from 0.90–1.30 to 0.48–0.70 ms (the asleep kinematic bodies cost the
  physics nothing). In Algeria at step 0's ping (262 against 255 ms, 28.5 ticks ahead against 23.8) Fusion's part of a
  long frame fell from 46.8 to 23.7 ms, the long frame from 76 to 59 ms.
- What reaches the screen: frames over 50 ms in Algeria 600 in 2:19 → 19 in 6:46 (10 in 2:28 at 143 ms), in Grimly
  Ridge 468 in 1:53 → 16 in 1:56, in Asgard 29 in 2:06 → 54 in 6:53; 25.2–27.8 fps → 29.2–29.8, the 1 % low 13.3–20.4
  → 21.3–29.4. A guest's frames are now about a host's (the same log as the host: Algeria 15 frames over 50 ms in 7:42).
  The CPU's long frames, about 59 ms now, mostly stay off the screen (233 of them against 19 frames over 50 ms in
  Algeria's second visit); at 72–76 ms in step 0 nearly all of them reached it.
- What is left is the host's lag. In Algeria's second visit the car stepped in 466 frames of 10,594 (4.4 %), 328 of them
  in the 70 s when HostMeter called the host falling behind or shaky (the lead at Fusion's limit of a second, 25–46 % of
  the frames stepped, the car off by up to 9.8 m); with the host keeping up, 0–2 steps in ten seconds. In Asgard, with
  the other players' cars back in the simulation near the guest, the scripts cost 0.8–1.0 ms a tick again, as expected.
- Once, in Asgard (10:55:16 by the log's clock, the guest's car stopping near the other players), the game's
  `VehicleWinchSystem.FixedUpdateNetwork` threw a NullReferenceException 7 times within ten seconds. Not seen before,
  but the game's errors are logged only since step 0. GuestLite had put no car in or out in the 20 s before it (the
  censuses at 10:55:08 and 10:55:18 the same), and the winch has no answer to leaving or entering the simulation (no
  `SimulationExit` or `SimulationEnter` in `VehicleWinchSystem`; its rope code takes a kinematic winched body for a
  fixed anchor). The update reads the rope's Obi objects, the car's body and the winched body; which one was null the
  log does not say.

## What runs on a guest (from the code, corrected by step 0)

- Measured on 17 November (NetDiag 0.4.1, `Latest (33).log`), against what the reading of the code had said: a guest
  simulates every car of the session – its own, the other players' and the wrecks – each in the simulation
  (`NetworkObject.IsInSimulation`), with a dynamic, awake body and its Vehicle Physics Pro update every tick, the
  resimulated ones included. Only the small networked props are kinematic proxies (Fusion warns of each: `The
  NetworkRigidbody … is NOT simulated on the local client`). How the cars get into a guest's simulation was not found in
  the code (`SimulationBehaviourUpdater::InvokeFixedUpdateNetwork` checks the InSimulation flag, the wrecks are spawned
  with no input authority, and the game never calls `NetworkRunner.SetIsSimulated`); the measurement settles that they
  are in it. So Algeria's dearer tick is its wrecks: the simulation's scripts cost 1.59 ms a tick on a guest there with
  10 cars against 1.25–1.29 ms in Grimly Ridge and Asgard with 6, and 1.38 ms against 0.77–0.78 ms on the host (8 cars
  against 3), about 0.1 ms a car; the physics costs about the same everywhere, 0.9–1.5 ms a tick.
- Each tick, the first resimulated ones included: the own car's chain (`VehicleBase.FixedUpdateNetwork` – Vehicle
  Physics Pro's update and `SaveSnapshot`), one PhysX step of the whole scene (`RunnerSimulatePhysics3D.SimulatePrimaryScene`
  under `ClientPhysicsSimulation = SimulateAlways`), and Fusion's copying of every object's state.
- `SimulateForward` (physics on forward ticks only) leaves the own car at the old pose through a resimulation: it
  bounces and loses the steering (OTH-LAN tried it and went back). Not a way.

## The levers a guest has

1. **Smoothing of the own car's corrections** (the game's own addition to Fusion's `NetworkRigidbodyBase`):
   `CorrectionSmoothingTime` (0.1 s), `CorrectionSnapDistance` (2 m), `CorrectionSnapAngle` (30°), per car, read every
   frame. A correction under the snap distance and angle is drawn as an offset that fades over the smoothing time; a
   larger one is a teleport in one frame – the 9 m jumps. The physics always takes the correction at once; only the
   drawn car is smoothed. Client-only and safe. Helps when the host keeps up; little when it stalls.
2. **Input delay** (Fusion's time sync, set by the client itself): the client keeps a delay of
   max(MinimumInputDelay, round trip − MaxResimulations × tick) and recomputes it on every snapshot
   (`ClientTimeProvider.UpdateTargetInputDelay`, settings in `ClientTimeProvider._settings`). A tick more of delay is a
   tick less to simulate again on every snapshot (~2 ms in Algeria) and smaller corrections, for 20 ms more between a
   key and the car. It covers the round trip, not the jitter: in Algeria ~20 ticks of the jitter stay uncovered.
3. **The game's own correction log**: `NetworkRigidbodyBase.LogPredictionCorrections` (with a minimum distance and
   angle) writes each correction with its resimulated ticks, millimetres and degrees. It goes through `Debug.Log`, so
   no Fusion log level stands in its way. Read in the build of 31 October (`NetworkRigidbody`2.EvaluatePredictionCorrection`,
   called once per correction from `BeforeAllTicks` or `ApplyCorrectionSmoothing`): the addon compares the car's
   predicted pose with the snapshot's, carrying the prediction over the gap between their ticks by its velocity only
   when the gap is at most four ticks. Past that the line says `NOT EXTRAPOLATED`, is written whatever the minimum
   distance and angle, and the correction is never smoothed: the drawn car takes it in one frame. Below that, a
   correction over `CorrectionSnapDistance` or `CorrectionSnapAngle`, or with `CorrectionSmoothingTime` at 0, is not
   smoothed either. So lever 1 can act only on the corrections whose gap is four ticks or less; step 0 counts how many
   of a guest's corrections that is.
4. Not from the client: a host that falls behind (Asgard). HostMeter shows it; the cure is another host or hosting.

## Plan for a guest mod

0. Measure, change nothing – done 17 November 2026 (NetDiag 0.4.1, "Step 0, measured" above). It overturned the plan's
   premise: a guest simulates every car, the wrecks too; the car steps little while the host keeps up; the
   resimulation's cost is what hurts. The order after it:
1. Far cars out of the guest's simulation: each wreck and other player's car more than ~150 m from the own car taken out
   with `NetworkRunner.SetIsSimulated(object, false)` and its body made kinematic (drawn from the snapshots like any
   proxy), put back before the own car comes near (a wreck to tow, a car to winch). Expect in Algeria 10 → 1–3 cars
   simulated, the scripts of a tick 1.6 → ~0.9 ms, ~14 ms less in a long frame there and ~6 ms in Canada, maybe less
   physics too. Check with NetDiag: the census, the ticks line, the frames over 50 ms; the owner tows a wreck.
   Read in the code (build of 31 October): `NetworkRunner.SetIsSimulated` refuses only in Shared mode; the change clears
   or sets the object's InSimulation flag (`ObjectIsSimulatedChanged`) and tells its `ISimulationExit` or
   `ISimulationEnter` behaviours; ``NetworkRigidbody`2`` answers the exit by making its body kinematic and has no answer
   to the enter, so whoever puts a car back gives its body back its dynamics; otherwise the flag is set only at spawn
   (`SimulationBehaviourUpdater.AddObject`). Built as GuestLite 0.1.0 (17 November 2026): every 0.5 s on a guest, out
   beyond 150 m, back within 120 m, the wrecks and the players' cars (`PlayersCarsToo`), all back if this game takes the
   session over. Played the same day ("Step 1, measured" above): the tick halved, a guest's frames are a host's.
   GuestLite 0.1.1 logs each car out and back, with its winch's state, and the car's state a pass after it came back.
   Played the same day (`~/Downloads/Latest (35).log`, Algeria, 14 minutes as a guest): a wreck back at 119 m was
   dynamic a pass later, then winched and towed about 500 m for 3.5 minutes with no error (the host then took it away and
   spawned another wreck near where it had stood, which GuestLite took out at 527 m and put back at 118 m, dynamic too).
   The winch's NullReferenceException came 3 times as the guest first put the winch out, with only the guest's own car in
   the simulation and GuestLite idle for 2:44: the game's own error, not this mod's. The winch makes its rope in `Render`
   (change detection, `ClientOnWinchStateChanged` → `CreateObiRopeGameObject`), which Fusion runs for a car out of the
   simulation too: taking a car out leaves its rope to the game.
2. Input delay, A against B – left out after step 1: a resimulated tick costs ~1 ms now, so a tick more of delay saves
   ~1 ms of a long frame that, at about 59 ms, mostly stays off the screen anyway, for 20 ms more between a key and the
   car. Back on the list only if the long frames reach the screen again.
3. Smoothing of the own car's corrections: snap distance 2 → 10 m, angle 30 → 90°, smoothing 0.1 → 0.2 s. Only the
   extrapolated corrections (tick gap at most 4); the rest the game never smooths. In Algeria's second visit of step 1:
   5 extrapolated corrections over the snap limits in 6:46, against 10 not extrapolated over 0.5 m that it cannot reach.
4. A drawing-only bridge over a host's stall (the car drawn on from its last motion for a few hundred milliseconds).
   After step 1 this is most of what a guest still sees: 328 of the 466 stepped frames in Algeria's second visit came in
   the 70 s of a host falling behind. Read in the code (build of 31 October): `Simulation.CalculateForwardTicks` runs
   the client's ticks up to the time provider's target but never past `LatestServerTick` plus the runtime config's tick
   rate – a second, in the code, not a setting; raising it would mean patching Fusion's core, whose buffers are sized
   around it. At the limit no tick runs, so the predicted car stands, and the missed ticks run at once when the state
   comes. `NetworkRigidbody`2.Render` draws the interpolated pose plus `_correctionOffsetPosition` and
   `_correctionOffsetRotation` (`ApplyCorrectionSmoothing`: first multiplied by exp(−deltaTime / `CorrectionSmoothingTime`),
   then added; the offset is drawn only for a predicted object without state authority, and only if the smoothing time
   is over 0); `EvaluatePredictionCorrection` adds a smoothed correction to that offset and resets it to zero for one it
   does not smooth. Fusion's default updater (`NetworkRunnerUpdaterDefault`) runs its simulation before Unity's
   `ScriptRunBehaviourUpdate` and its render after it. Built as GuestLite 0.2.0 (17 November 2026), on by default, not
   played yet: in `Update`, when the car this player drives is at the limit and moving at 1.5 m/s or more, the offset is
   written so that after the render's fade the car is drawn carried on by its velocity turning with its angular
   velocity, for up to `BridgeMaxSeconds` (0.5 s), the game's own offset of that moment fading on under it; when a tick
   runs again, the car is drawn from where it was carried to and eased to the game's car (time constant 0.15 s), the
   game's own new corrections added in. Check: `the bridge works: …` in the log; NetDiag's stepped frames in a session
   with a lagging host against Algeria's second visit of step 1; the stalls' lengths in GuestLite's summary. Played the
   same day (`~/Downloads/Latest (36).log`, six visits as a guest): `the bridge works: … 0.71 m of 0.71 m`, the offset
   found at 0x134 and 0x140 as read; the hosts kept up (HostMeter never red), so in 18 minutes in Algeria it drew over
   only 8 stalls, all of 0.1 s or less, the car 8 cm from the game's on average when Fusion went on, at most 14 cm. Its
   first stall cost one frame of 135 ms (GuestLite's update 94 ms in one call, the bridge's code compiled on its first
   run): GuestLite 0.2.1 compiles it ahead while the game joins a session. Not met yet: a host falling behind for
   seconds, the bridge's real case.
5. The dangling parts (the owner, 18 November 2026: an antenna that swings in jerks as a guest, and likely mud flaps,
   pans and the like). The game's prefabs (`game/probe/prefab_Car_*.txt`) hang them as Rigidbodies on joints under the
   car, none networked: `AntennaRoot/AntennaBone` (ConfigurableJoint), `MudflapRoot/MudflapBone` and its `.001`
   (HingeJoint, a chain of two), `WhipLightRoot/WhipLightBone`, `ShackleRoot/ShackleBone`, `RopeRoot/RopeBoneL`/`R`
   (CharacterJoint), `Rear/Pan`, `CampingLantern/Lantern`, `ToilerSeatArmature/SeatBone`. Under `SimulateAlways` a
   guest's resimulation steps the whole scene and puts back only the networked bodies, so these get the resimulated
   ticks on top of the forward ones: 1.7 forward and 2.4–4.4 resimulated a frame on 17 November (`Latest (36).log`),
   3.15 in step 0's Algeria before any of this mod – not new, only seen once the frames were smooth. A host never
   resimulates (`Latest (37).log`, alone: 0). Built as GuestLite 0.3.0 (18 November 2026), on by default
   (`DanglingParts`), not played yet: every 0.5 s the cars in this game's simulation are looked through for such bodies
   (active, not kinematic, on a joint, of no networked object of their own; again every 10 s); after Fusion's ticks of
   every frame each part's pose, velocity and angular velocity relative to its car's body are kept; a Harmony prefix on
   `RunnerSimulatePhysics3D.SimulatePrimaryScene` notes the resimulated ticks (`NetworkRunner.IsResimulation`) and, before
   the first forward tick after them, puts each part back where it was kept relative to where its car is now (its
   transform and its body), swinging as it was; a resimulation with no forward tick after it is put back after the
   frame's ticks. Check: the parts named in the log, `first resimulation with the dangling parts put back`, and the
   owner's eye on an antenna and the mud flaps as a guest. Played 18 November 2026 (`~/Downloads/Latest (39).log`, four
   visits as a guest, 58 minutes): 9 parts on the driven Protector (`MudflapBone x4, MudflapBone.001 x4, AntennaBone`),
   up to 33 on 3 cars; put back after 27 537 resimulations in all, about 8 a second; the resimulations had carried them
   1–2 cm off on average, 7–10 cm at most, once 90 cm. The owner: the antenna swings right. GuestLite's update cost
   about 0.1 ms a frame with it.

OTH-LAN's notes say a client simulates every car, and step 0 agrees. Fusion's warning `NetworkRigidbody … is NOT
simulated on the local client` comes 5–7 times a visit, as many times as there are networked props found not simulated.
