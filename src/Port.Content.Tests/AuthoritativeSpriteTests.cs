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
}
