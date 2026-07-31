using System.Text;
using Port.Net;
using Robust.Shared.GameObjects;

namespace Port.Content.Tests;

public sealed class AuthoritativeSpriteTests
{
    [Fact]
    public void PrototypeYamlResolvesInheritedOrderedLayers()
    {
        var root = Path.Combine(Path.GetTempPath(), "port-proto-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "Prototypes");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "entities.yml"), """
                - type: entity
                  id: BaseMachine
                  components:
                  - type: Sprite
                    sprite: Structures/Machines/base.rsi
                    noRot: true
                    layers:
                    - state: base
                    - state: screen
                      color: "#112233"
                      offset: 0.25, -0.5
                - type: entity
                  id: ChildMachine
                  parent: BaseMachine
                  components:
                  - type: Sprite
                    state: powered
                """);

            var index = new PrototypeSpriteIndex();
            index.EnsureLoaded(root);
            var sprite = index.TryGetResolvedSprite("ChildMachine");

            Assert.NotNull(sprite);
            Assert.Equal("Structures/Machines/base.rsi", sprite.Path);
            Assert.Equal("powered", sprite.State);
            Assert.True(sprite.NoRotation);
            Assert.Collection(sprite.Layers,
                layer => Assert.Equal("base", layer.State),
                layer =>
                {
                    Assert.Equal("screen", layer.State);
                    Assert.Equal((byte)0x11, layer.R);
                    Assert.Equal((byte)0x22, layer.G);
                    Assert.Equal((byte)0x33, layer.B);
                    Assert.Equal(0.25f, layer.OffsetX);
                    Assert.Equal(-0.5f, layer.OffsetY);
                });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingRequestedRsiStateNeverSubstitutes()
    {
        var exact = new RsiAtlas.StateInfo
        {
            Name = "computer",
            DirCount = 1,
            Delays = [new[] { 1f }],
            SheetOffset = 0,
            TotalFrames = 1,
        };
        var atlas = new RsiAtlas.Loaded
        {
            SourcePath = "machines.rsic",
            FrameW = 32,
            FrameH = 32,
            AtlasW = 32,
            AtlasH = 32,
            DimX = 1,
            States = new(StringComparer.OrdinalIgnoreCase) { ["computer"] = exact },
            FrameCounts = [1],
            StateOrder = ["computer"],
        };

        var missing = RsiAtlas.Sample(atlas, "chair", 0, 0);
        Assert.Equal(0, missing.FrameW);
        Assert.Equal(0, missing.FrameH);

        var nullState = RsiAtlas.Sample(atlas, null, 0, 0);
        Assert.Equal(0, nullState.FrameW);
        Assert.Equal(0, nullState.FrameH);

        var found = RsiAtlas.Sample(atlas, "computer", 0, 0);
        Assert.Equal(32, found.FrameW);
        Assert.Equal(32, found.FrameH);
    }

    [Fact]
    public void EmptyNetworkSpriteDoesNotWipePrototypeLayers()
    {
        var ent = new NetEntity(42);
        var proto = new GameStateDecoder.SpriteVisual
        {
            FromNetwork = false,
            Path = "Structures/Machines/computer.rsi",
            State = "computer",
        };
        proto.Layers.Add(new GameStateDecoder.LayerVis("Structures/Machines/computer.rsi", "computer", 0, true, 255, 255, 255));
        proto.Layers.Add(new GameStateDecoder.LayerVis("Structures/Machines/computer.rsi", "keyboard", 0, true, 255, 255, 255));
        proto.Layers.Add(new GameStateDecoder.LayerVis("Structures/Machines/computer.rsi", "screen", 0, true, 255, 255, 255));
        var sprites = new Dictionary<NetEntity, GameStateDecoder.SpriteVisual>
        {
            [ent] = proto,
        };

        // Sparse network state: Visible only, no layers — must keep YAML stack.
        GameStateDecoder.TryExtractSpritePublic(new SpriteComponentState { Visible = true }, ent, sprites);

        Assert.True(sprites.TryGetValue(ent, out var vis));
        Assert.Equal(3, vis.Layers.Count);
        Assert.Equal("computer", vis.Layers[0].State);
        Assert.Equal("keyboard", vis.Layers[1].State);
        Assert.Equal("screen", vis.Layers[2].State);
        Assert.Equal("Structures/Machines/computer.rsi", vis.Path);
    }

    [Fact]
    public void SparseNetworkLayersDoNotShrinkPrototypeStack()
    {
        var ent = new NetEntity(7);
        var proto = new GameStateDecoder.SpriteVisual
        {
            FromNetwork = false,
            Path = "Structures/Machines/computers.rsi",
        };
        proto.Layers.Add(new GameStateDecoder.LayerVis("Structures/Machines/computers.rsi", "computer", 0, true, 255, 255, 255, MapKey: "computerLayerBody"));
        proto.Layers.Add(new GameStateDecoder.LayerVis("Structures/Machines/computers.rsi", "generic_key", 0, true, 255, 255, 255, MapKey: "computerLayerKeyboard"));
        proto.Layers.Add(new GameStateDecoder.LayerVis("Structures/Machines/computers.rsi", "virology", 0, true, 255, 255, 255, MapKey: "computerLayerScreen"));
        var sprites = new Dictionary<NetEntity, GameStateDecoder.SpriteVisual> { [ent] = proto };

        GameStateDecoder.TryExtractSpritePublic(new SpriteComponentState
        {
            Visible = true,
            Layers =
            [
                new NetLayer { State = "virology", Visible = true },
            ],
        }, ent, sprites);

        Assert.True(sprites.TryGetValue(ent, out var vis));
        Assert.Equal(3, vis.Layers.Count);
        Assert.Equal("computer", vis.Layers[0].State);
        Assert.Equal("generic_key", vis.Layers[1].State);
        Assert.Equal("virology", vis.Layers[2].State);
    }

    [Fact]
    public void IconSmoothInfersSolidBaseFromMetaJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "port-meta-" + Guid.NewGuid().ToString("N"));
        var rsi = Path.Combine(root, "Textures", "Structures", "Walls", "solid.rsi");
        Directory.CreateDirectory(rsi);
        try
        {
            File.WriteAllText(Path.Combine(rsi, "meta.json"), """
                {
                  "version": 1,
                  "size": { "x": 32, "y": 32 },
                  "states": [
                    { "name": "full" },
                    { "name": "solid0", "directions": 4 },
                    { "name": "solid1", "directions": 4 },
                    { "name": "solid2", "directions": 4 },
                    { "name": "solid3", "directions": 4 },
                    { "name": "solid4", "directions": 4 },
                    { "name": "solid5", "directions": 4 },
                    { "name": "solid6", "directions": 4 },
                    { "name": "solid7", "directions": 4 }
                  ]
                }
                """);
            for (var i = 0; i <= 7; i++)
                File.WriteAllBytes(Path.Combine(rsi, $"solid{i}.png"), [0x89, 0x50, 0x4E, 0x47]);

            IconSmoothInfer.ClearCache();
            var data = IconSmoothInfer.FromRsi(root, "Structures/Walls/solid.rsi", "WallSolid");
            Assert.NotNull(data);
            Assert.Equal("solid", data!.Value.StateBase);
            Assert.Equal("walls", data.Value.Key);
            Assert.Equal(IconSmoothMode.Corners, data.Value.Mode);
            Assert.True(RsiMeta.LooksLikeIconSmoothStateName("solid3"));
            Assert.True(RsiMeta.LooksLikeIconSmoothStateName("riveted12"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ChairAndLockerStatesComeFromPrototypeYaml()
    {
        var root = Path.Combine(Path.GetTempPath(), "port-furn-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "Prototypes");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "furniture.yml"), """
                - type: entity
                  id: SeatBase
                  components:
                  - type: Sprite
                    sprite: Structures/Furniture/chairs.rsi
                    noRot: true
                - type: entity
                  id: Chair
                  parent: SeatBase
                  components:
                  - type: Sprite
                    state: chair
                - type: entity
                  id: ChairBrass
                  parent: SeatBase
                  components:
                  - type: Sprite
                    state: brass_chair
                - type: entity
                  id: ClosetBase
                  components:
                  - type: Sprite
                    sprite: Structures/Storage/closet.rsi
                    layers:
                    - state: generic
                      map: ["enum.StorageVisualLayers.Base"]
                    - state: generic_door
                      map: ["enum.StorageVisualLayers.Door"]
                    - state: welded
                      visible: false
                      map: ["enum.WeldableLayers.BaseWelded"]
                  - type: EntityStorageVisuals
                    stateBaseClosed: generic
                    stateDoorOpen: generic_open
                    stateDoorClosed: generic_door
                - type: entity
                  id: LockerBlueShield
                  parent: ClosetBase
                  components:
                  - type: EntityStorageVisuals
                    stateBaseClosed: blueshield
                    stateDoorClosed: blueshield_door
                    stateDoorOpen: blueshield_open
                """);

            var index = new PrototypeSpriteIndex();
            index.EnsureLoaded(root);
            Assert.Equal("chair", index.TryGetState("Chair"));
            Assert.Equal("brass_chair", index.TryGetState("ChairBrass"));
            Assert.Equal("Structures/Furniture/chairs.rsi", index.TryGetSprite("Chair"));

            var locker = index.TryGetResolvedSprite("LockerBlueShield");
            Assert.NotNull(locker);
            Assert.Equal(3, locker!.Layers.Count);
            Assert.Equal("generic", locker.Layers[0].State); // Sprite layers until storage apply
            var storage = index.TryGetStorageVisuals("LockerBlueShield");
            Assert.NotNull(storage);
            Assert.Equal("blueshield", storage!.Value.StateBaseClosed);
            Assert.Equal("blueshield_door", storage.Value.StateDoorClosed);
            Assert.False(locker.Layers[2].Visible); // welded hidden
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    sealed class NetLayer
    {
        public string? State { get; set; }
        public bool Visible { get; set; }
    }

    // Name must contain SpriteComponent so TryExtractSprite matches.
    sealed class SpriteComponentState
    {
        public bool Visible { get; set; }
        public string? RSI { get; set; }
        public List<object>? Layers { get; set; }
    }

    [Fact]
    public void WallSolidStyleIconSmoothUsesBaseAndKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "port-wall-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "Prototypes");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "walls.yml"), """
                - type: entity
                  id: BaseStructureWall
                  components:
                  - type: Sprite
                    drawdepth: Walls
                  - type: Icon
                    state: full
                - type: entity
                  id: WallSolid
                  parent: BaseStructureWall
                  components:
                  - type: Sprite
                    sprite: Structures/Walls/solid.rsi
                  - type: Icon
                    sprite: Structures/Walls/solid.rsi
                    state: full
                  - type: IconSmooth
                    key: walls
                    base: solid
                - type: entity
                  id: WallRiveted
                  components:
                  - type: Sprite
                    sprite: Structures/Walls/riveted.rsi
                  - type: IconSmooth
                    key: walls
                    base: riveted
                - type: entity
                  id: Airlock
                  components:
                  - type: Sprite
                    sprite: Structures/Doors/Airlocks/Standard/basic.rsi
                    layers:
                    - state: closed
                      map: ["enum.DoorVisualLayers.Base"]
                    - state: welded
                      map: ["enum.WeldableLayers.BaseWelded"]
                    - state: bolted_unlit
                      map: ["enum.DoorVisualLayers.BaseBolted"]
                    - state: panel_open
                      map: ["enum.WiresVisualLayers.MaintenancePanel"]
                """);

            var index = new PrototypeSpriteIndex();
            index.EnsureLoaded(root);
            var solid = index.TryGetIconSmooth("WallSolid");
            var riveted = index.TryGetIconSmooth("WallRiveted");
            Assert.NotNull(solid);
            Assert.Equal("solid", solid!.Value.StateBase);
            Assert.Equal("walls", solid.Value.Key);
            Assert.NotNull(riveted);
            Assert.Equal("riveted", riveted!.Value.StateBase);
            Assert.Equal("Structures/Walls/solid.rsi", index.TryGetSprite("WallSolid"));
            Assert.Equal("Structures/Walls/riveted.rsi", index.TryGetSprite("WallRiveted"));

            // Icon must NOT poison Sprite with state:full (was killing IconSmooth + furniture).
            var wallSprite = index.TryGetResolvedSprite("WallSolid");
            Assert.NotNull(wallSprite);
            Assert.Equal("Structures/Walls/solid.rsi", wallSprite!.Path);
            Assert.Null(wallSprite.State);
            Assert.Equal(-2, wallSprite.DrawDepth); // Walls enum

            var airlock = index.TryGetResolvedSprite("Airlock");
            Assert.NotNull(airlock);
            Assert.Equal(4, airlock!.Layers.Count);
            Assert.True(airlock.Layers[0].Visible);  // closed base
            Assert.False(airlock.Layers[1].Visible); // welded default hidden
            Assert.False(airlock.Layers[2].Visible); // bolted default hidden
            Assert.False(airlock.Layers[3].Visible); // panel default hidden
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ManifestIndexesPackedAndExplodedRsiAssets()
    {
        var manifest = """
            Robust Content Manifest 1
            aa Assemblies/Content.Shared.dll
            bb Prototypes/Entities/test.yml
            cc Textures/Structures/test.rsic
            dd Textures/Structures/raw.rsi/meta.json
            ee Textures/Structures/raw.rsi/exact.png
            """;
        var plan = ManifestPlan.Extract(Encoding.UTF8.GetBytes(manifest));
        var index = plan.BuildTextureIndex();

        Assert.Contains("Textures/Structures/test.rsic", index.Keys);
        Assert.Contains("Textures/Structures/raw.rsi/meta.json", index.Keys);
        Assert.Contains("Textures/Structures/raw.rsi/exact.png", index.Keys);
    }

    [Theory]
    [InlineData(0f, 0)]          // East angle → South RSI (PC GetDirection)
    [InlineData(1.5707964f, 2)]  // North → East RSI
    [InlineData(3.1415927f, 1)]  // West → North RSI
    [InlineData(-1.5707964f, 3)] // South → West RSI
    public void DirectionIndexMatchesPcSpriteBias(float radians, int expectedDir)
    {
        Assert.Equal(expectedDir, RsiAtlas.DirectionIndex(radians, 4));
    }

    [Fact]
    public void IconSmoothParsesAdditionalKeysAndMode()
    {
        var root = Path.Combine(Path.GetTempPath(), "port-smooth-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "Prototypes");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "walls.yml"), """
                - type: entity
                  id: WallWithExtras
                  components:
                  - type: Sprite
                    sprite: Structures/Walls/solid.rsi
                  - type: IconSmooth
                    key: walls
                    base: solid
                    additionalKeys:
                    - windows
                    - grilles
                - type: entity
                  id: ConnectorOnly
                  components:
                  - type: IconSmooth
                    key: walls
                    base: solid
                    mode: NoSprite
                """);

            var index = new PrototypeSpriteIndex();
            index.EnsureLoaded(root);
            var wall = index.TryGetIconSmooth("WallWithExtras");
            Assert.NotNull(wall);
            Assert.Equal("walls", wall!.Value.Key);
            Assert.Equal("solid", wall.Value.StateBase);
            Assert.Equal(IconSmoothMode.Corners, wall.Value.Mode);
            Assert.Equal(new[] { "windows", "grilles" }, wall.Value.AdditionalKeys);

            var connector = index.TryGetIconSmooth("ConnectorOnly");
            Assert.NotNull(connector);
            Assert.Equal(IconSmoothMode.NoSprite, connector!.Value.Mode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IconSmoothDirOverrideNeverAnimates()
    {
        var state = new RsiAtlas.StateInfo
        {
            Name = "solid0",
            DirCount = 4,
            Delays =
            [
                new[] { 0.1f, 0.1f, 0.1f },
                new[] { 0.1f, 0.1f, 0.1f },
                new[] { 0.1f, 0.1f, 0.1f },
                new[] { 0.1f, 0.1f, 0.1f },
            ],
            SheetOffset = 0,
            TotalFrames = 12,
        };
        var atlas = new RsiAtlas.Loaded
        {
            SourcePath = "solid.rsic",
            FrameW = 32,
            FrameH = 32,
            AtlasW = 128,
            AtlasH = 96,
            DimX = 4,
            States = new(StringComparer.OrdinalIgnoreCase) { ["solid0"] = state },
            FrameCounts = [12],
            StateOrder = ["solid0"],
        };

        var a = RsiAtlas.Sample(atlas, "solid0", 0, 0.0, dirOverride: 0);
        var b = RsiAtlas.Sample(atlas, "solid0", 0, 0.5, dirOverride: 0);
        Assert.Equal(a.U0, b.U0);
        Assert.Equal(a.V0, b.V0);
    }

    [Theory]
    [InlineData(0f, 0f, 1f)]
    [InlineData(1.5707964f, -1f, 0f)]
    [InlineData(3.1415927f, 0f, -1f)]
    public void GridCameraRotatesScreenUpWithoutUsingEntityFacing(
        float cameraRotation, float expectedX, float expectedY)
    {
        var (x, y) = GridCameraMath.RotateScreenInput(0, 1, cameraRotation);
        Assert.InRange(x, expectedX - 0.0001f, expectedX + 0.0001f);
        Assert.InRange(y, expectedY - 0.0001f, expectedY + 0.0001f);
    }

    [Fact]
    public void AirlockNoSpriteKeepsYamlLayersAfterComposeGate()
    {
        var root = Path.Combine(Path.GetTempPath(), "port-airlock-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "Prototypes");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "airlock.yml"), """
                - type: entity
                  id: Airlock
                  components:
                  - type: Sprite
                    sprite: Structures/Doors/Airlocks/standard.rsi
                    snapCardinals: true
                    layers:
                    - state: closed
                      map: ["enum.DoorVisualLayers.Base"]
                    - state: closed_unlit
                      map: ["enum.DoorVisualLayers.BaseUnlit"]
                    - state: welded
                      map: ["enum.WeldableLayers.BaseWelded"]
                  - type: IconSmooth
                    key: walls
                    mode: NoSprite
                    # no base — PC airlocks are neighbor-only
                """);

            var index = new PrototypeSpriteIndex();
            index.EnsureLoaded(root);
            var smooth = index.TryGetIconSmooth("Airlock");
            Assert.NotNull(smooth);
            Assert.Equal(IconSmoothMode.NoSprite, smooth!.Value.Mode);
            Assert.False(WorldStateCache.IsDrawableIconSmooth(smooth.Value));

            var resolved = index.TryGetResolvedSprite("Airlock");
            Assert.NotNull(resolved);
            Assert.True(resolved!.SnapCardinals);
            Assert.Equal(3, resolved.Layers.Count);
            Assert.Equal("closed", resolved.Layers[0].State);
            Assert.True(resolved.Layers[0].Visible);
            Assert.False(resolved.Layers[1].Visible); // door unlit overlay
            Assert.Equal("welded", resolved.Layers[2].State);
            Assert.False(resolved.Layers[2].Visible);

            // Compose path: NoSprite must keep YAML stack (not path-only wipe).
            var visual = new GameStateDecoder.SpriteVisual { FromNetwork = false };
            visual.Path = resolved.Path;
            visual.SnapCardinals = resolved.SnapCardinals;
            foreach (var layer in resolved.Layers)
            {
                visual.Layers.Add(new GameStateDecoder.LayerVis(
                    layer.Path ?? resolved.Path, layer.State, 0, layer.Visible,
                    255, 255, 255, 0, 0, false, 1f, 1f, 0f, layer.MapKey));
            }

            Assert.Equal(3, visual.Layers.Count);
            Assert.Contains(visual.Layers, l => l.State == "closed" && l.Visible);
            Assert.Contains(visual.Layers, l => l.State == "welded" && !l.Visible);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TechFabUnlitVisibleInsertingHiddenByDefault()
    {
        Assert.False(PrototypeSpriteIndex.IsDefaultHiddenOverlay(null, "unlit"));
        Assert.True(PrototypeSpriteIndex.IsDefaultHiddenOverlay(null, "inserting"));
        Assert.True(PrototypeSpriteIndex.IsDefaultHiddenOverlay("enum.MaterialStorageVisualLayers.Inserting", "inserting"));
        Assert.True(PrototypeSpriteIndex.IsDefaultHiddenOverlay(null, "closed_unlit"));

        var root = Path.Combine(Path.GetTempPath(), "port-lathe-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "Prototypes");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "techfab.yml"), """
                - type: entity
                  id: TechFab
                  components:
                  - type: Sprite
                    sprite: Structures/Machines/techfab.rsi
                    layers:
                    - state: icon
                    - state: unlit
                    - state: inserting
                      map: ["enum.LatheVisualLayers.IsRunning"]
                """);

            var index = new PrototypeSpriteIndex();
            index.EnsureLoaded(root);
            var sprite = index.TryGetResolvedSprite("TechFab");
            Assert.NotNull(sprite);
            Assert.Collection(sprite!.Layers,
                layer =>
                {
                    Assert.Equal("icon", layer.State);
                    Assert.True(layer.Visible);
                },
                layer =>
                {
                    Assert.Equal("unlit", layer.State);
                    Assert.True(layer.Visible);
                },
                layer =>
                {
                    Assert.Equal("inserting", layer.State);
                    Assert.False(layer.Visible);
                });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ComputerAppearanceTogglesScreenWithoutWipingStack()
    {
        var ent = new NetEntity(9);
        var proto = new GameStateDecoder.SpriteVisual
        {
            FromNetwork = false,
            Path = "Structures/Machines/computers.rsi",
        };
        proto.Layers.Add(new GameStateDecoder.LayerVis(
            "Structures/Machines/computers.rsi", "computer", 0, true, 255, 255, 255,
            MapKey: "enum.ComputerVisualLayers.Body"));
        proto.Layers.Add(new GameStateDecoder.LayerVis(
            "Structures/Machines/computers.rsi", "generic_keys", 0, true, 255, 255, 255,
            MapKey: "enum.ComputerVisualLayers.Keyboard"));
        proto.Layers.Add(new GameStateDecoder.LayerVis(
            "Structures/Machines/computers.rsi", "computer_keyboard", 0, true, 255, 255, 255,
            MapKey: "computerLayerKeys"));
        proto.Layers.Add(new GameStateDecoder.LayerVis(
            "Structures/Machines/computers.rsi", "virology", 0, true, 255, 255, 255,
            MapKey: "computerLayerScreen"));

        var sprites = new Dictionary<NetEntity, GameStateDecoder.SpriteVisual> { [ent] = proto };
        GameStateDecoder.TryExtractSpritePublic(new SpriteComponentState { Visible = true }, ent, sprites);
        Assert.Equal(4, sprites[ent].Layers.Count);

        AppearanceVisuals.ApplyToSprite(sprites[ent], new Dictionary<string, string>
        {
            ["ComputerVisuals.Powered"] = "False",
        }, doorState: null);

        Assert.Equal(4, sprites[ent].Layers.Count);
        Assert.True(sprites[ent].Layers[0].Visible); // body
        Assert.False(sprites[ent].Layers[2].Visible); // keys
        Assert.False(sprites[ent].Layers[3].Visible); // screen

        AppearanceVisuals.ApplyToSprite(sprites[ent], new Dictionary<string, string>
        {
            ["ComputerVisuals.Powered"] = "True",
        }, doorState: null);
        Assert.True(sprites[ent].Layers[2].Visible);
        Assert.True(sprites[ent].Layers[3].Visible);
    }

    [Fact]
    public void LightGlowFollowsAppearanceBulbOn()
    {
        var visual = new GameStateDecoder.SpriteVisual { FromNetwork = false };
        visual.Layers.Add(new GameStateDecoder.LayerVis(
            "Structures/Wallmounts/light.rsi", "base", 0, true, 255, 255, 255,
            MapKey: "enum.PoweredLightVisualLayers.Base"));
        visual.Layers.Add(new GameStateDecoder.LayerVis(
            "Structures/Wallmounts/light.rsi", "glow", 0, true, 255, 255, 255,
            MapKey: "enum.PoweredLightVisualLayers.Glow"));

        AppearanceVisuals.ApplyToSprite(visual, new Dictionary<string, string>
        {
            ["PoweredLightVisuals.Bulb"] = "Off",
        }, null);
        Assert.Equal("base", visual.Layers[0].State);
        Assert.False(visual.Layers[1].Visible);

        AppearanceVisuals.ApplyToSprite(visual, new Dictionary<string, string>
        {
            ["PoweredLightVisuals.Bulb"] = "On",
        }, null);
        Assert.Equal("base", visual.Layers[0].State);
        Assert.True(visual.Layers[1].Visible);
    }

    [Fact]
    public void LightBaseStateAliasesNormal()
    {
        Assert.Contains("normal", RsiMeta.PreferredStateAlternates("base"));
        Assert.Contains("base", RsiMeta.PreferredStateAlternates("normal"));
    }

    [Fact]
    public void DamagedGrilleStateFallsBackToLowerTiers()
    {
        var alts = RsiMeta.PreferredStateAlternates("grille_damaged_4").ToList();
        Assert.Contains("grille_damaged_4", alts);
        Assert.Contains("grille_damaged_3", alts);
        Assert.Contains("grille_damaged_0", alts);
        Assert.Contains("grille_broken", alts);
        Assert.Contains("grille", alts);
        // IconSmooth solidN must not get damage-tier clamping.
        Assert.Equal(new[] { "solid3" }, RsiMeta.PreferredStateAlternates("solid3").ToArray());
    }

    [Fact]
    public void IconSmoothFolderDirOverrideSamplesDistinctCells()
    {
        // solid0.png layout: 4 dirs × 32px = 128×32. DirOverride must not collapse to dir0.
        var state = new RsiAtlas.StateInfo
        {
            Name = "solid0",
            DirCount = 1, // broken meta — still must sample distinct cells via sheet size
            Delays = [new[] { 1f }],
            SheetOffset = 0,
            TotalFrames = 1,
        };
        var atlas = new RsiAtlas.Loaded
        {
            SourcePath = "solid.rsi",
            FrameW = 32,
            FrameH = 32,
            AtlasW = 32, // LoadFolder often seeds from full.png — override must win
            AtlasH = 32,
            DimX = 1,
            States = new(StringComparer.OrdinalIgnoreCase) { ["solid0"] = state },
            FrameCounts = [1],
            StateOrder = ["solid0"],
        };

        var s = RsiAtlas.Sample(atlas, "solid0", 0, 0, folderPerStateSheet: true, dirOverride: 0,
            overrideAtlasW: 128, overrideAtlasH: 32);
        var e = RsiAtlas.Sample(atlas, "solid0", 0, 0, folderPerStateSheet: true, dirOverride: 2,
            overrideAtlasW: 128, overrideAtlasH: 32);
        Assert.True(s.FrameW >= 1f);
        Assert.True(e.FrameW >= 1f);
        Assert.NotEqual(s.U0, e.U0);
    }

    [Fact]
    public void IconSmoothPackedDirCountOneWithFourFramesUsesDirOverride()
    {
        var state = new RsiAtlas.StateInfo
        {
            Name = "solid0",
            DirCount = 1,
            Delays = [new[] { 1f }],
            SheetOffset = 0,
            TotalFrames = 4,
        };
        var atlas = new RsiAtlas.Loaded
        {
            SourcePath = "solid.rsic",
            FrameW = 32,
            FrameH = 32,
            AtlasW = 128,
            AtlasH = 32,
            DimX = 4,
            States = new(StringComparer.OrdinalIgnoreCase) { ["solid0"] = state },
            FrameCounts = [4],
            StateOrder = ["solid0"],
        };

        var a = RsiAtlas.Sample(atlas, "solid0", 0, 0, dirOverride: 0);
        var b = RsiAtlas.Sample(atlas, "solid0", 0, 0, dirOverride: 3);
        Assert.NotEqual(a.U0, b.U0);
    }

    [Fact]
    public void ChildIconSmoothBaseOnlyMergesParentKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "port-swin-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "Prototypes");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "windows.yml"), """
                - type: entity
                  id: Window
                  components:
                  - type: Sprite
                    sprite: Structures/Windows/window.rsi
                  - type: IconSmooth
                    key: walls
                    base: window
                - type: entity
                  id: ShuttleWindow
                  parent: Window
                  components:
                  - type: Sprite
                    sprite: Structures/Windows/shuttle_window.rsi
                  - type: IconSmooth
                    base: swindow
                """);

            var index = new PrototypeSpriteIndex();
            index.EnsureLoaded(root);
            var child = index.TryGetIconSmooth("ShuttleWindow");
            Assert.NotNull(child);
            Assert.Equal("walls", child!.Value.Key);
            Assert.Equal("swindow", child.Value.StateBase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RailingSpriteNotInferredAsIconSmooth()
    {
        var root = Path.Combine(Path.GetTempPath(), "port-rail-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "Textures", "Structures", "Walls", "railing.rsi");
        var dir = Path.Combine(root, "Prototypes");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(folder, "meta.json"), """
                {
                  "version": 1,
                  "size": { "x": 32, "y": 32 },
                  "states": [
                    { "name": "side", "directions": 4 },
                    { "name": "corner", "directions": 4 },
                    { "name": "round", "directions": 4 }
                  ]
                }
                """);
            File.WriteAllText(Path.Combine(dir, "railing.yml"), """
                - type: entity
                  id: Railing
                  components:
                  - type: Sprite
                    sprite: Structures/Walls/railing.rsi
                    state: side
                """);

            IconSmoothInfer.ClearCache();
            var inferred = IconSmoothInfer.FromRsi(root, "Structures/Walls/railing.rsi", "Railing");
            Assert.Null(inferred);

            var index = new PrototypeSpriteIndex();
            index.EnsureLoaded(root);
            Assert.Null(index.TryGetIconSmooth("Railing"));
            var sprite = index.TryGetResolvedSprite("Railing");
            Assert.NotNull(sprite);
            Assert.Equal("side", sprite!.State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            IconSmoothInfer.ClearCache();
        }
    }

    [Fact]
    public void WindowIconSmoothPrefersRwindowAlias()
    {
        Assert.Contains("rwindow1", RsiMeta.PreferredStateAlternates("window1"));
        Assert.Contains("window1", RsiMeta.PreferredStateAlternates("rwindow1"));

        var root = Path.Combine(Path.GetTempPath(), "port-rwin-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "Textures", "Structures", "Windows", "reinforced_window.rsi");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "meta.json"), """
                {
                  "version": 1,
                  "size": { "x": 32, "y": 32 },
                  "states": [
                    { "name": "full" },
                    { "name": "rwindow0", "directions": 4 },
                    { "name": "rwindow1", "directions": 4 },
                    { "name": "rwindow7", "directions": 4 }
                  ]
                }
                """);
            File.WriteAllBytes(Path.Combine(folder, "rwindow1.png"), [0x89, 0x50, 0x4E, 0x47]);
            File.WriteAllBytes(Path.Combine(folder, "full.png"), [0x89, 0x50, 0x4E, 0x47]);

            IconSmoothInfer.ClearCache();
            RsiAtlas.ClearCache();
            Assert.False(IconSmoothInfer.RsiHasNumberedBase(root, "Structures/Windows/reinforced_window.rsi", "window"));
            Assert.True(IconSmoothInfer.RsiHasNumberedBase(root, "Structures/Windows/reinforced_window.rsi", "rwindow"));

            var inferred = IconSmoothInfer.FromRsi(root, "Structures/Windows/reinforced_window.rsi", "ReinforcedWindow");
            Assert.NotNull(inferred);
            Assert.Equal("rwindow", inferred!.Value.StateBase);

            var src = RsiMeta.FindRsiSource(root, "Structures/Windows/reinforced_window.rsi", "window1");
            Assert.NotNull(src);
            Assert.False(src!.Value.IsRsic);
            var frame = RsiMeta.TryGetPreviewFrame(src.Value.Path, "window1");
            Assert.NotNull(frame);
            Assert.Contains("rwindow1", frame!.Value.PngPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            IconSmoothInfer.ClearCache();
            RsiAtlas.ClearCache();
        }
    }

    [Fact]
    public void RsiAtlasDoesNotCacheNullMisses()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-rsi-" + Guid.NewGuid().ToString("N") + ".rsic");
        Assert.Null(RsiAtlas.TryLoad(missing));
        // Create a minimal invalid file path still missing — second call must retry, not sticky-null.
        Assert.Null(RsiAtlas.TryLoad(missing));

        var root = Path.Combine(Path.GetTempPath(), "port-rsic-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "Textures", "Structures", "Furniture", "chairs.rsi");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "meta.json"), """
                {
                  "version": 1,
                  "size": { "x": 32, "y": 32 },
                  "states": [ { "name": "chair" }, { "name": "sofa" } ]
                }
                """);
            // Tiny invalid PNG header is enough for size peek failure → folder still loads meta.
            File.WriteAllBytes(Path.Combine(dir, "chair.png"), [0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0]);

            RsiAtlas.ClearCache();
            var loaded = RsiAtlas.TryLoad(dir);
            Assert.NotNull(loaded);
            Assert.True(loaded!.States.ContainsKey("chair"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            RsiAtlas.ClearCache();
        }
    }

    [Fact]
    public void FindRsiSourceFallsThroughWhenRsicLacksPreferredState()
    {
        var root = Path.Combine(Path.GetTempPath(), "port-src-" + Guid.NewGuid().ToString("N"));
        var tex = Path.Combine(root, "Textures", "Structures", "Walls");
        var folder = Path.Combine(tex, "solid.rsi");
        Directory.CreateDirectory(folder);
        try
        {
            // Empty/corrupt .rsic placeholder that parses to an atlas WITHOUT solid3.
            // We simulate via folder-only: no .rsic, folder has solid3.png.
            File.WriteAllText(Path.Combine(folder, "meta.json"), """
                {
                  "version": 1,
                  "size": { "x": 32, "y": 32 },
                  "states": [
                    { "name": "full" },
                    { "name": "solid0", "directions": 4 },
                    { "name": "solid3", "directions": 4 }
                  ]
                }
                """);
            File.WriteAllBytes(Path.Combine(folder, "solid3.png"), [0x89, 0x50, 0x4E, 0x47]);
            File.WriteAllBytes(Path.Combine(folder, "full.png"), [0x89, 0x50, 0x4E, 0x47]);

            RsiAtlas.ClearCache();
            var src = RsiMeta.FindRsiSource(root, "Structures/Walls/solid.rsi", "solid3");
            Assert.NotNull(src);
            Assert.False(src!.Value.IsRsic);
            Assert.Contains("solid.rsi", src.Value.Path, StringComparison.OrdinalIgnoreCase);

            var frame = RsiMeta.TryGetPreviewFrame(src.Value.Path, "solid3");
            Assert.NotNull(frame);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            RsiAtlas.ClearCache();
        }
    }

    [Fact]
    public void TileParentInheritsSpriteForAsteroidSand()
    {
        var root = Path.Combine(Path.GetTempPath(), "port-tiles-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "Prototypes", "Tiles");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "asteroid.yml"), """
                - type: tile
                  id: FloorAsteroidSandBorderless
                  name: sand-borderless
                  sprite: /Textures/Tiles/Asteroid/asteroid.png
                - type: tile
                  id: FloorAsteroidSand
                  parent: FloorAsteroidSandBorderless
                  name: sand
                - type: tile
                  id: Space
                  name: space
                """);

            var index = new TilePrototypeIndex();
            index.EnsureLoaded(root);
            Assert.Equal("Tiles/Asteroid/asteroid.png", index.TryGetSpriteById("FloorAsteroidSandBorderless"));
            Assert.Equal("Tiles/Asteroid/asteroid.png", index.TryGetSpriteById("FloorAsteroidSand"));
            // Space is typeId 0; concrete tiles get 1..n in Ordinal order.
            Assert.True(index.Count >= 2);
            var sandId = (ushort)0;
            // Find sand typeId via sprite presence on all slots.
            string? found = null;
            for (ushort i = 1; i <= index.Count; i++)
            {
                var s = index.TryGetSprite(i);
                if (s == "Tiles/Asteroid/asteroid.png")
                {
                    found = s;
                    sandId = i;
                }
            }
            Assert.Equal("Tiles/Asteroid/asteroid.png", found);
            Assert.True(sandId > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NetworkStatelessLayersDoNotWipeDoorStack()
    {
        var ent = new NetEntity(11);
        var proto = new GameStateDecoder.SpriteVisual
        {
            FromNetwork = false,
            Path = "Structures/Doors/Airlocks/standard.rsi",
            SnapCardinals = true,
        };
        proto.Layers.Add(new GameStateDecoder.LayerVis(
            "Structures/Doors/Airlocks/standard.rsi", "closed", 0, true, 255, 255, 255,
            MapKey: "enum.DoorVisualLayers.Base"));
        proto.Layers.Add(new GameStateDecoder.LayerVis(
            "Structures/Doors/Airlocks/standard.rsi", "welded", 0, false, 255, 255, 255,
            MapKey: "enum.WeldableLayers.BaseWelded"));
        var sprites = new Dictionary<NetEntity, GameStateDecoder.SpriteVisual> { [ent] = proto };

        GameStateDecoder.TryExtractSpritePublic(new SpriteComponentState
        {
            Visible = true,
            Layers =
            [
                new NetLayer { Visible = true },
                new NetLayer { Visible = false },
            ],
        }, ent, sprites);

        Assert.Equal(2, sprites[ent].Layers.Count);
        Assert.Equal("closed", sprites[ent].Layers[0].State);
        Assert.True(sprites[ent].SnapCardinals);
    }

    [Fact]
    public void FindRsiSourceKeepsOneDirNumberedStatesInRsic()
    {
        var root = Path.Combine(Path.GetTempPath(), "port-grille-" + Guid.NewGuid().ToString("N"));
        var tex = Path.Combine(root, "Textures", "Structures", "Walls");
        Directory.CreateDirectory(tex);
        try
        {
            // Minimal fake .rsic is hard; use folder with meta that has 1-dir numbered state
            // and ensure PreferredStateAlternates / Find still resolves when only folder exists.
            var folder = Path.Combine(tex, "grille.rsi");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "meta.json"), """
                {
                  "version": 1,
                  "size": { "x": 32, "y": 32 },
                  "states": [
                    { "name": "grille" },
                    { "name": "grille_damaged_0" },
                    { "name": "grille_damaged_1" }
                  ]
                }
                """);
            File.WriteAllBytes(Path.Combine(folder, "grille_damaged_0.png"), [0x89, 0x50, 0x4E, 0x47]);
            RsiAtlas.ClearCache();
            var src = RsiMeta.FindRsiSource(root, "Structures/Walls/grille.rsi", "grille_damaged_0");
            Assert.NotNull(src);
            Assert.False(src!.Value.IsRsic);
            var frame = RsiMeta.TryGetPreviewFrame(src.Value.Path, "grille_damaged_0");
            Assert.NotNull(frame);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiagLogCollapsesIdenticalConsecutiveLines()
    {
        var collapsed = DiagLog.CollapseRepeated(
        [
            "03:12:39.572 [I] iconsmooth base remap ReinforcedWindow: 'window' → 'rwindow'",
            "03:12:39.572 [I] iconsmooth base remap ReinforcedWindow: 'window' → 'rwindow'",
            "03:12:39.574 [I] iconsmooth base remap ReinforcedWindow: 'window' → 'rwindow'",
            "03:12:40.014 [W] gles noTex Objects/Power/light_tube.rsi | base",
            "03:27:09.856 [I] Transfer: MsgTransferData #1063",
            "03:27:09.856 [I] Transfer: MsgTransferData #1064",
            "03:27:09.857 [I] Transfer: MsgTransferData #1065",
        ]);

        Assert.Contains("iconsmooth base remap ReinforcedWindow: 'window' → 'rwindow' x3", collapsed);
        Assert.Contains("[W] gles noTex Objects/Power/light_tube.rsi | base", collapsed);
        Assert.Contains("Transfer: MsgTransferData #1063 x3", collapsed);
        Assert.Equal(3, collapsed.Split('\n').Length);
    }

    [Fact]
    public void DiagLogCollapsesDeserPayloadSizes()
    {
        var collapsed = DiagLog.CollapseRepeated(
        [
            "04:05:10.741 [E] Apply FAIL DESER IndexOutOfRangeException: Arg_IndexOutOfRangeException (payload=7,078B storeXf=141)",
            "04:05:11.178 [E] Apply FAIL DESER IndexOutOfRangeException: Arg_IndexOutOfRangeException (payload=9,243B storeXf=540)",
            "04:05:11.725 [E] Apply FAIL DESER IndexOutOfRangeException: Arg_IndexOutOfRangeException (payload=6,128B storeXf=790)",
        ]);
        Assert.Contains("x3", collapsed);
        Assert.Equal(1, collapsed.Split('\n').Length);
    }

    [Fact]
    public void AuthoritativeModeSkipsPathHeuristicWhenMetaMissing()
    {
        var prev = SpriteResolveOptions.AuthoritativeOnly;
        SpriteResolveOptions.AuthoritativeOnly = true;
        try
        {
            IconSmoothInfer.ClearCache();
            // No content root / meta — path invent must not fire in authoritative mode.
            var data = IconSmoothInfer.FromRsi(null, "Structures/Walls/solid.rsi", "WallSolid");
            Assert.Null(data);

            // Explicit opt-in still allows legacy invent for tests / rollback.
            var legacy = IconSmoothInfer.FromRsi(
                null, "Structures/Walls/solid.rsi", "WallSolid", allowPathHeuristic: true);
            Assert.NotNull(legacy);
            Assert.Equal("solid", legacy!.Value.StateBase);
        }
        finally
        {
            SpriteResolveOptions.AuthoritativeOnly = prev;
            IconSmoothInfer.ClearCache();
        }
    }
}
