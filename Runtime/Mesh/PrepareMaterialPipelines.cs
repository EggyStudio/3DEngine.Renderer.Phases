namespace Engine;

/// <summary>
/// Prepare-stage system that walks every <see cref="RenderMeshInstance"/> in the
/// render world, and for each unique <see cref="MaterialHandle"/> not yet present
/// in <see cref="MaterialPipelineRegistry"/> runs the MaterialX → GLSL → SPIR-V
/// pipeline once and caches the result. Downstream draw functions inspect the
/// registry to choose the per-material pipeline or the shared fallback.
/// </summary>
/// <remarks>
/// The system is idempotent on a per-material basis: an entry is processed at
/// most once and its <see cref="MaterialPipelineStatus"/> latches to
/// <see cref="MaterialPipelineStatus.Ready"/>, <see cref="MaterialPipelineStatus.Failed"/>,
/// or <see cref="MaterialPipelineStatus.NoMaterialX"/>. Hot-reload of a material
/// (asset event) is the trigger that should evict and re-process its entry; that
/// hook is intentionally not wired in this slice.
/// </remarks>
public sealed class PrepareMaterialPipelines : IPrepareSystem
{
    private static readonly ILogger Logger = Log.Category("Engine.Renderer.Materials");

    /// <inheritdoc />
    public void Run(RenderWorld renderWorld, RenderContext renderContext)
    {
        var ecs = renderWorld.Entities;
        if (ecs.Count<RenderMeshInstance>() == 0) return;

        var registry = renderWorld.TryGet<MaterialPipelineRegistry>();
        if (registry is null)
        {
            registry = new MaterialPipelineRegistry();
            renderWorld.Set(registry);
        }

        foreach (var (_, mesh) in ecs.Query<RenderMeshInstance>())
        {
            var handle = mesh.Material;
            if (!handle.IsValid) continue;

            int id = handle.Id;
            if (registry.TryGet(id) is not null) continue;

            var entry = new MaterialPipelineEntry { MaterialId = id };
            try
            {
                var desc = handle.GetDescription();
                if (string.IsNullOrEmpty(desc.MaterialXSource))
                {
                    entry.Status = MaterialPipelineStatus.NoMaterialX;
                    Logger.Debug($"PrepareMaterialPipelines: material id={id} '{desc.Name}' has no MaterialX source; fallback pipeline applies.");
                }
                else
                {
                    var generated = MaterialXShaderGenerator.Generate(desc.MaterialXSource, shaderName: desc.Name);
                    if (generated is null)
                    {
                        entry.Status = MaterialPipelineStatus.Failed;
                        entry.FailureReason = "MaterialX shader generation returned null.";
                    }
                    else
                    {
                        var spv = MaterialXSpirv.Compile(generated, fileNameHint: desc.Name);
                        if (spv is null)
                        {
                            entry.Status = MaterialPipelineStatus.Failed;
                            entry.FailureReason = "Generated GLSL failed to compile to SPIR-V.";
                            entry.Generated = generated;
                        }
                        else
                        {
                            entry.Status = MaterialPipelineStatus.Ready;
                            entry.Generated = generated;
                            entry.Spirv = spv;
                            Logger.Debug(
                                $"PrepareMaterialPipelines: material id={id} '{desc.Name}' ready " +
                                $"(vert={spv.VertexSpirv.Length}B, frag={spv.FragmentSpirv.Length}B, bindings={generated.Bindings.Count}).");
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                entry.Status = MaterialPipelineStatus.Failed;
                entry.FailureReason = ex.Message;
                Logger.Warn($"PrepareMaterialPipelines: material id={id} threw during preparation: {ex.Message}");
            }

            registry.Set(entry);
        }
    }
}

