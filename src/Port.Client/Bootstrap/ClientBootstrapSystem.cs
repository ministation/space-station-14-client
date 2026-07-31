using Port.Client.Content;
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
    public sealed record BootResult(
        ClientLoop Loop,
        AndroidUiHost Ui,
        ClydeRenderSystem? Render,
        ContentClientLoadSystem ContentClient,
        ContentClientGameplaySystem Gameplay);

    public static BootResult CreateDefaultLoop(
        PrototypeSpriteIndex? prototypes = null,
        ClydeRenderSystem? render = null)
    {
        var loop = new ClientLoop();
        var ui = new AndroidUiHost();
        var contentClient = new ContentClientLoadSystem();
        var gameplay = new ContentClientGameplaySystem { LoadSystem = contentClient };
        loop.Add(ui);
        loop.Add(contentClient);
        loop.Add(gameplay);
        if (prototypes is not null)
            loop.Add(new SpritePipelineSystem(new AuthoritativeSpritePipeline(prototypes)));
        if (render is not null)
            loop.Add(render);
        return new BootResult(loop, ui, render, contentClient, gameplay);
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
