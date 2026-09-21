# Sound

`FruitLib.FruitSfx` hands you the game's own sound effects, so a mod can sound
like part of the game rather than like something playing over it.

## Interface sounds

```csharp
FruitSfx.PlayUI(UISFXType.LargeButtonClick);
```

Plays through the game's own service, which means it lands on the game's mixer and
obeys the master volume the player set. A mod that played UI sounds through an
`AudioSource` of its own would ignore the audio settings, which is exactly the
sort of detail that makes something feel bolted on.

Useful members of `UISFXType`:

| Value | Used by the game for |
|---|---|
| `LargeButtonClick` | Pause menu line buttons |
| `SmallButtonClick` | Rows inside a settings table |
| `SwitchOn` / `SwitchOff` | Toggles |
| `ToolbarItemSwitch` | Changing the held tool |
| `WindowOpenClose` | A window appearing or going away |
| `HintButtonClick` | Hint prompts |

FruitLib already plays these for its own menu and for toolbar slot changes — you
do not need to play them for anything FruitLib draws.

## Gameplay sounds

Four categories, each keyed by the game's own enum:

```csharp
AudioClip shot = FruitSfx.Weapon(WeaponSFXType.Shoot9MM);
AudioClip hit  = FruitSfx.Impact(ImpactSFXType.Flesh);
```

| Call | Enum |
|---|---|
| `FruitSfx.Weapon(...)` | `WeaponSFXType` |
| `FruitSfx.Impact(...)` | `ImpactSFXType` |
| `FruitSfx.Whoosh(...)` | `WhooshSFXType` |
| `FruitSfx.Tools(...)` | `ToolsSFXType` |

These hand back an `AudioClip` for you to play through your own `AudioSource`,
which is the point: the service's own play methods take a long list of parameters
and, in practice, ignore the position you give them, playing everything on the
listener. Fetching the asset and driving your own source gives you real 3D
placement and pitch control.

It also fixes availability. Assets reached this way exist from startup, whereas
scanning with `Resources.FindObjectsOfTypeAll<AudioClip>` only finds one after the
game has played it once.

### Picking a specific take

Each `*Resource` method returns the game's `FAudioResource`, which holds a list of
interchangeable takes:

```csharp
FAudioResource res = FruitSfx.ImpactResource(ImpactSFXType.Flesh);
AudioClip      one = res.Pick();      // a take, chosen per call
IReadOnlyList<AudioClip> all = res.Clips;
```

`FruitSfx.Impact(...)` is `ImpactResource(...).Pick()`. Because the take is chosen
per call, hold the returned clip if you want the same one twice.

## Availability

The sound service is built by Zenject and is not findable directly, so FruitSfx
locates it by way of something it was injected into. Until it does, every call
returns null or does nothing — it never throws.

That matters at startup and immediately after a scene change. Ask for a clip when
you need it rather than caching one at load, or cache it lazily and retry while it
is null. GunsGunsGuns does the latter, with a retry throttle so a missing sound
does not cost a scene search per bullet.
