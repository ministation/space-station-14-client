using Port.Client.Rendering;
using Port.Client.Sprites;
using Port.Client.Ui;
using Port.Content;

namespace Port.Client.Bootstrap;

/// <summary>
/// Wires foundation systems for the full Android client.
/// </summary>
public static class ClientBootstrap
{
    public sealed record BootResult(ClientLoop Loop, AndroidUiHost Ui, ClydeRenderSystem? Render);

    public static BootResult CreateDefaultLoop(
        PrototypeSpriteIndex? prototypes = null,
        ClydeRenderSystem? render = null)
    {
        var loop = new ClientLoop();
        var ui = new AndroidUiHost();
        loop.Add(ui);
        if (prototypes is not null)
            loop.Add(new SpritePipelineSystem(new AuthoritativeSpritePipeline(prototypes)));
        if (render is not null)
            loop.Add(render);
        return new BootResult(loop, ui, render);
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
