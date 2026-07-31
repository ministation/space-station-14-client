using Port.Client;
using Port.Client.Bootstrap;
using Port.Client.Content;
using Port.Client.Rendering;
using Port.Client.Sprites;
using Port.Client.Ui;
using Port.Content;
using Port.Net;

namespace Port.Client.Tests;

public class ClientFoundationTests
{
    [Fact]
    public void FeatureFlagsDelegateToSharedPolicies()
    {
        var prevAuth = SpriteResolveOptions.AuthoritativeOnly;
        var prevStrict = SpriteResolveOptions.StrictRsiStates;
        var prevLoad = SerializerLoadPolicy.LoadContentClientAssemblies;
        try
        {
            ClientFeatureFlags.AuthoritativeSprites = false;
            ClientFeatureFlags.StrictRsiStates = false;
            ClientFeatureFlags.LoadContentClientAssemblies = true;
            Assert.False(SpriteResolveOptions.AuthoritativeOnly);
            Assert.False(SpriteResolveOptions.StrictRsiStates);
            Assert.True(SerializerLoadPolicy.LoadContentClientAssemblies);
        }
        finally
        {
            SpriteResolveOptions.AuthoritativeOnly = prevAuth;
            SpriteResolveOptions.StrictRsiStates = prevStrict;
            SerializerLoadPolicy.LoadContentClientAssemblies = prevLoad;
        }
    }

    [Fact]
    public void ClientLoopRunsSystemsInOrder()
    {
        var loop = new ClientLoop();
        var order = new List<string>();
        loop.Add(new ProbeSystem("a", order));
        loop.Add(new ProbeSystem("b", order));
        loop.Start();
        Assert.Equal(["a:init", "b:init"], order);
        loop.FrameUpdate(0.016f);
        Assert.Equal(["a:init", "b:init", "a:frame", "b:frame"], order);
        loop.Shutdown();
        Assert.Equal(["a:init", "b:init", "a:frame", "b:frame", "b:shutdown", "a:shutdown"], order);
        Assert.False(loop.IsRunning);
    }

    [Fact]
    public void AuthoritativePipelinePrefersYamlOverHeuristic()
    {
        var root = Path.Combine(Path.GetTempPath(), "port-pipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Prototypes"));
        try
        {
            File.WriteAllText(Path.Combine(root, "Prototypes", "wall.yml"), """
                - type: entity
                  id: WallSolid
                  components:
                  - type: Sprite
                    sprite: Structures/Walls/solid.rsi
                  - type: IconSmooth
                    key: walls
                    base: solid
                    mode: Corners
                """);
            var index = new PrototypeSpriteIndex();
            index.EnsureLoaded(root);
            var pipe = new AuthoritativeSpritePipeline(index);
            ClientFeatureFlags.AuthoritativeSprites = true;
            var data = pipe.ResolveIconSmooth("WallSolid", "Structures/Walls/solid.rsi", root);
            Assert.NotNull(data);
            Assert.Equal("solid", data!.Value.StateBase);
            Assert.Equal("walls", data.Value.Key);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ContentAssemblyHostSkipsClientPacksByDefault()
    {
        Assert.False(ContentAssemblyHost.ShouldLoad("Content.Client.dll"));
        Assert.True(ContentAssemblyHost.ShouldLoad("Content.Shared.dll"));
        var prev = SerializerLoadPolicy.LoadContentClientAssemblies;
        try
        {
            SerializerLoadPolicy.LoadContentClientAssemblies = true;
            Assert.True(ContentAssemblyHost.ShouldLoad("Content.Client.dll"));
        }
        finally
        {
            SerializerLoadPolicy.LoadContentClientAssemblies = prev;
        }
    }

    [Fact]
    public void AndroidUiHostExposesUiManager()
    {
        var host = new AndroidUiHost();
        host.Initialize();
        Assert.NotNull(host.Ui);
        host.FrameUpdate(0.05f);
        host.Shutdown();
    }

    [Fact]
    public void ObserveHudRootIsUiManagerRoot()
    {
        var host = new AndroidUiHost();
        host.Initialize();
        Assert.Same(host.ObserveHud, host.Ui.RootControl);
        host.ObserveHud.SetStatus("ghost · z1.0");
        host.ObserveHud.SetFps("60 FPS");
        Assert.Equal("ghost · z1.0", host.ObserveHud.StatusLabel.Text);
        Assert.Equal("60 FPS", host.ObserveHud.FpsLabel.Text);
        host.Input.SetKey(Robust.Client.Input.Keyboard.Key.W, true);
        Assert.True(host.Input.IsKeyDown(Robust.Client.Input.Keyboard.Key.W));
        host.FrameUpdate(0.05f);
        Assert.False(host.Input.WasKeyPressed(Robust.Client.Input.Keyboard.Key.W));
        host.Shutdown();
    }

    [Fact]
    public void ClydeRenderSystemSyncsCameraToEye()
    {
        var view = new NullClydeWorldView();
        var render = new ClydeRenderSystem(view);
        render.CameraSource = () => (10f, 20f, 0.5f, 2f);
        render.Initialize();
        render.FrameUpdate(0.05f);
        Assert.Equal(10f, view.Camera.X);
        Assert.Equal(20f, view.Camera.Y);
        Assert.Equal(2f, view.Zoom);
        Assert.Equal(10f, render.Eyes.CurrentEye!.Position.X);
        render.Shutdown();
    }

    [Fact]
    public void RobustClientStubShellsConstruct()
    {
        var gc = new Robust.Client.GameController();
        gc.Startup();
        Assert.True(gc.IsRunning);
        gc.Tick(0.016f);
        gc.Shutdown();
        Assert.False(gc.IsRunning);

        var clyde = new Robust.Client.Graphics.NullClyde();
        clyde.FrameProcess(0.016f);
        Assert.Equal(1, clyde.ScreenSize.X);

        var eyes = new Robust.Client.Graphics.EyeManager();
        Assert.NotNull(eyes.CurrentEye);

        var input = new Robust.Client.Input.InputManager();
        Assert.False(input.IsKeyDown(Robust.Client.Input.Keyboard.Key.W));

        _ = new Robust.Client.GameObjects.SpriteSystem();
    }

    sealed class ProbeSystem(string name, List<string> order) : IClientSystem
    {
        public void Initialize() => order.Add(name + ":init");
        public void FrameUpdate(float dt) => order.Add(name + ":frame");
        public void Shutdown() => order.Add(name + ":shutdown");
    }
}
