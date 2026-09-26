using System.Drawing;
using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.Runtime.InProcess;

internal static partial class Program
{
    private static void RunPartColorScenario(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        if (scenario == "color-expanded-palette")
        {
            var allocated = new List<Color>();
            for (var number = 1; number <= 121; number++)
            {
                var color = LayerService.ChoosePartColor(number, true, allocated);
                Require(allocated.All(existing => existing.ToArgb() != color.ToArgb()), "New categories must never repeat an allocated color.");
                if (number <= LayerService.DefaultPartColors.Length)
                    Require(color.ToArgb() == LayerService.DefaultPartColors[number - 1].ToArgb(), "The first 21 category colors must retain the predefined palette.");
                else
                    Require(!LayerService.DefaultPartColors.Any(existing => existing.ToArgb() == color.ToArgb()), "Expanded colors must not wrap to the palette.");
                allocated.Add(color);
            }
            Require(LayerService.ChoosePartColor(100, false, allocated).ToArgb() == Color.Black.ToArgb(), "Disabled colorization must remain black beyond the palette.");
            Require(LayerService.PartColorForName("P22").ToArgb() == LayerService.PartColorForName("P22").ToArgb(), "The stateless legacy fallback must remain stable.");
            return;
        }
        if (scenario == "color-component-disabled")
        {
            services.ColorizeParts = false;
            try
            {
                var firstDocument = fixtureDocs.Count;
                RunComponentUpdateScenario(core, services, "component-add-new-part", fixtureDocs);
                var doc = fixtureDocs[firstDocument];
                var assembly = services.Repository.Load(doc).Assemblies.Single();
                var added = assembly.Parts.Single(part => part.Name == "P03");
                Require(added.LayerColorArgb == Color.Black.ToArgb(), "Newly added part colors must respect the disabled preference and persist as black.");
                Require(assembly.LinkGraph.Nodes.Where(node => node.PartId == added.Id && node.Role != AssemblyLinkRoles.Source)
                    .All(node => doc.Layers[doc.Objects.FindId(node.ObjectId).Attributes.LayerIndex].Color.ToArgb() == Color.Black.ToArgb()),
                    "All generated stages of a newly added part must be black when colorization is off.");
            }
            finally { services.ColorizeParts = true; }
            return;
        }

        var document = RhinoDoc.Create(null);
        fixtureDocs.Add(document);
        using var mutation = AssemblyLinkMutationGate.Enter();
        var record = new AssemblyRecord { Name = scenario };
        var old = new PartRecord { Name = "P01", LayerColorArgb = Color.Red.ToArgb() };
        var addedPart = new PartRecord { Name = "P02" };
        record.Parts.AddRange(new[] { old, addedPart });
        var customized = LayerService.DefaultPartColors[1];
        services.Layers.EnsurePartLayerIndex(document, LayerService.OriginalPart(record.Name, "C01", old.Name), customized);
        var assigned = services.Layers.GetOrAssignPartColor(document, record, addedPart);
        Require(assigned.ToArgb() != customized.ToArgb(), "A new category must avoid live custom colors even if saved legacy colors are stale.");
        Require(old.LayerColorArgb == customized.ToArgb(), "Live custom category colors must become the saved authority.");
        var overflow = new PartRecord { Name = "P22" };
        record.Parts.Add(overflow);
        var overflowColor = services.Layers.GetOrAssignPartColor(document, record, overflow);
        services.Repository.Save(document, new AssemblyStore { Assemblies = new List<AssemblyRecord> { record } });
        var restored = services.Repository.Load(document).FindAssembly(record.Name)!;
        var saved = restored.Parts.Single(part => part.Id == overflow.Id);
        Require(saved.LayerColorArgb == overflowColor.ToArgb(), "Random category colors must survive repository serialization.");
        for (var repeat = 0; repeat < 5; repeat++)
            Require(services.Layers.GetOrAssignPartColor(document, restored, saved).ToArgb() == overflowColor.ToArgb(),
                "Missing output layers must reuse the persisted category color, not randomize again.");
        foreach (var path in new[] { LayerService.OriginalPart(record.Name, "C01", saved.Name), LayerService.CopiedComponentPart(record.Name, "C01", saved.Name), LayerService.PartsPart(record.Name, saved.Name) + "::3D" })
        {
            var index = services.Layers.EnsurePartLayerIndex(document, path, services.Layers.GetOrAssignPartColor(document, restored, saved));
            Require(document.Layers[index].Color.ToArgb() == overflowColor.ToArgb(), "Recreated output stages must use the same saved color.");
        }
    }
}
