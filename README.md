# GuestLite – smoother co-op for a guest in Over the Hill

A MelonLoader mod for Over the Hill. It changes only the game of a player who joins someone else's co-op session:
nothing goes over the network or into the save, the host and the other players see no change, and it can be taken out
at any time. When you host or play alone it does nothing.

This repository holds its source for review. Version 0.3.0 here is the one released on Nexus Mods as "GuestLite
(Smoother Co-op as a Guest)": the released DLL was built from these very files.

## Why a guest lags

Over the Hill runs its co-op on Photon Fusion 2 in Host mode, 50 ticks a second. A guest's game simulates every car of
the session – its own, the other players' and the wrecks – and on every snapshot from the host it simulates them all
again over the ticks it runs ahead (8–16 ticks a snapshot at 175–256 ms of ping, 2.1–2.4 ms a tick, about 0.1 ms of it
a car). In Algeria, with 10 cars, 7 of them wrecks, that made frames of 58–76 ms. The host never resimulates – the main
reason the same map can run smoothly for the host and lag for its guests.

## What it does

Three parts, each with its own switch, all on by default.

**Far cars out of the simulation** (`Far.cs`). Every 0.5 s on a guest, each other car farther than 150 m from this
player's car is taken out of this game's simulation with Fusion's own switch for a client in Host mode,
`NetworkRunner.SetIsSimulated(object, false)`. Fusion's `NetworkRigidbody` then makes its body kinematic and draws it
from the host's snapshots, as it draws any proxy. Within 120 m the car is put back and its body given its dynamics
again, before this player's car can touch it, tow it or winch it. If this game takes the session over from a host who
left, every car goes back. A car Fusion puts back on its own is taken out again; after three such returns it is left
alone.

**The bridge over a host's stall** (`Bridge.cs`). Fusion never runs a guest more than a second of ticks past the host's
latest state (`Simulation.CalculateForwardTicks`: the host's tick plus the tick rate, in the code, not a setting). When
a host falls behind, the guest reaches that limit and runs no tick: the car stands on the screen, then jumps when the
host's state comes. The bridge draws the car on meanwhile as it went – its velocity, turning with its angular velocity –
for up to half a second, then holds it; when Fusion goes on, the car is drawn from there and eased to the game's car
(a time constant of 0.15 s). It only draws: it writes the game's own correction offset, the one
`NetworkRigidbody.Render` fades its corrections with (`_correctionOffsetPosition`, `_correctionOffsetRotation`, found by
name through il2cpp). Fusion simulates before the scripts' `Update` and draws after it, so the offset is drawn the same
frame; the bridge checks that order and stays off, saying why, if it is not so. It also checks once that the car really
went on on the screen while Fusion waited, and turns itself off if it did not.

**The dangling parts** (`Dangling.cs`). Antennas, mud flaps, whip lights, shackles, ropes, a hanging pan, a lantern:
each is its own Rigidbody on a joint under the car, not networked, so every game swings its own. A guest's resimulation
steps the whole scene's physics and puts back only the networked bodies, so these parts get the resimulated steps on
top and swing two to three and a half times too fast, in jerks at every snapshot. GuestLite keeps each part's pose,
velocity and angular velocity relative to its car after Fusion's ticks of every frame, and before the first forward tick
after a resimulation (a Harmony prefix on `RunnerSimulatePhysics3D.SimulatePrimaryScene`) puts it back there relative
to where its car is now. Only the parts of the cars in this game's simulation are touched, never the cars themselves.

## Measured

On the author's PC, as a guest in other people's sessions, with two diagnostic mods (NetDiag, HostMeter) that are not
published:

- Far cars out (0.1.0, 17 November 2026, five visits): a resimulated tick 2.2–2.5 ms → 0.9–1.3 ms. In Algeria at the
  same ping, frames over 50 ms went from 600 in 2:19 to 19 in 6:46, 25.5 → 29.7 fps – about what the host has.
- The bridge (0.3.0, 18 November 2026): in Algeria it drew over 26 stalls, 21 of them 0.1–0.25 s, the longest 0.40 s;
  when Fusion went on, the car was 12 cm from the game's on average.
- The dangling parts (0.3.0, 18 November 2026, four visits, 58 minutes): 9 parts on the driven car, up to 33 on 3 cars,
  put back after about 8 resimulations a second, which had carried them 1–2 cm off on average. GuestLite's update cost
  about 0.1 ms a frame.

## What it cannot do

- A slow host or a bad connection stays slow. The bridge covers a host's stall for half a second at most; a stall of
  seconds stays a stall past that.
- A car that hit something while Fusion waited is drawn through it, then eased back.
- Not met yet in play: a host falling behind for seconds (the bridge's real case), and another player's car on a winch
  while it is taken out or put back.
- The winch's `NullReferenceException` in `VehicleWinchSystem.FixedUpdateNetwork` is the game's own error: it came with
  only the guest's own car in the simulation and GuestLite idle.

## Files

- `src/GuestLite/Mod.cs` – the MelonLoader entry: the settings, the frame hooks (`OnUpdate`: Far, Bridge, Dangling;
  `OnLateUpdate`: Bridge) and the one Harmony patch.
- `src/GuestLite/Far.cs` – finds the runner and the cars on a guest, takes the far ones out and puts them back, logs
  each, and hands the cars in the simulation to the bridge and the dangling parts.
- `src/GuestLite/Bridge.cs` – detects a stall at Fusion's limit and draws the car on through its correction offset;
  the frame-order check, the self-check and the warm-up that compiles its code while the game joins a session.
- `src/GuestLite/Dangling.cs` – finds the jointed, non-networked bodies under the simulated cars, keeps their state
  relative to the car and puts it back after a resimulation.
- `docs/guest-netcode.md` – the working log behind the code: what the game's code and Fusion do, what was measured,
  why each step. "The owner" there is the mod's author, and the logs it names are the author's MelonLoader logs.

## Worth a reviewer's look

- Private fields read by name through il2cpp (`il2cpp_class_get_field_from_name`, `il2cpp_field_get_offset`) and written
  by offset (`Bridge.cs`). The bridge stays off if a field is not found.
- `SetIsSimulated` on objects this game does not own, and the body's dynamics given back on return (`Far.cs`): Fusion's
  `NetworkRigidbody` answers the exit by making the body kinematic but has no answer to the enter.
- The Harmony prefix runs before every physics step, the resimulated ones included, so it must stay cheap
  (`Dangling.cs`).
- A car taken out with its winch attached: the winch makes its rope in `Render`, which Fusion runs for an object out of
  the simulation too.

## Settings

`UserData/MelonPreferences.cfg`, section `[GuestLite]`:

| Setting | Default | What it does |
|---|---|---|
| `FarCarsOut` | true | Far cars leave this game's simulation; false keeps the game's way |
| `PlayersCarsToo` | true | The other players' far cars as well as the wrecks; false takes only the wrecks out |
| `OutBeyond` | 150 | Metres from this player's car beyond which a car leaves the simulation |
| `BackWithin` | 120 | Metres within which it comes back (below `OutBeyond`, so a car at the edge doesn't flick) |
| `BridgeStalls` | true | The bridge over a host's stall; false leaves the car standing, then jumping |
| `BridgeMaxSeconds` | 0.5 | Seconds the bridge draws the car on at most |
| `DanglingParts` | true | The dangling parts swing at their own pace; false leaves them swinging in jerks |

The mod writes what it does to MelonLoader's `Latest.log` under `[GuestLite]`: each car out and back with its distance,
whether the bridge is on and that it works, the parts found, and a summary on leaving a session.

## Building

1. The .NET 6 SDK (or newer).
2. Over the Hill with MelonLoader 0.7.3, started once, so that MelonLoader generates `MelonLoader/Il2CppAssemblies` in the
   game's folder.
3. That folder copied to `ref/Il2CppAssemblies` at this repository's root. It is not committed: the assemblies are
   generated from the game.
4. `dotnet build src/GuestLite/GuestLite.csproj -c Release` gives `src/GuestLite/bin/Release/net6.0/GuestLite.dll`, which
   goes into the game's `Mods` folder.

## Feedback

Comments and issues are welcome, especially on anything that could break a session or a save.
