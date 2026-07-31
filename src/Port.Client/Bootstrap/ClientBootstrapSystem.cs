using Port.Client.Rendering;
using Port.Client.Sprites;
using Port.Client.Ui;
using Port.Content;

namespace Port.Client.Bootstrap;

/// <summary>
/// Wires foundation systems for the full Android client. MainActivity should
/// migrate game logic into systems registered here over subsequent PRs.
/// </summary>
public static class ClientBootstrap
{
    public static ClientLoop CreateDefaultLoop(
        PrototypeSpriteIndex? prototypes = null,
        ClydeRenderSystem? render = null)
    {
        var loop = new ClientLoop();
        loop.Add(new AndroidUiHost());
        if (prototypes is not null)
            loop.Add(new SpritePipelineSystem(new AuthoritativeSpritePipeline(prototypes)));
        if (render is not null)
            loop.Add(render);
        return loop;
    }
}

sealed class SpritePipelineSystem(AuthoritativeSpritePipeline pipeline) : IClientSystem
{
    public AuthoritativeSpritePipeline Pipeline { get; } = pipeline;

    public void Initialize()
    {
        ClientFeatureFlags.AuthoritativeSprites = true;
        ClientFeatureFlags.StrictRsiStates = true;
    }

    public void FrameUpdate(float dt) { }

    public void Shutdown() { }
}
