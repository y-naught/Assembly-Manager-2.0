using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Runtime.InProcess;

internal static partial class Program
{
    private static void RunRegroupMaterialMergeScenario(RhinoCore core, RegressionServices services,
        string scenario, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = true;
        services.EnableLinkedAssemblies = true;
        var fixture = CreateComponentAdditionFixture(doc, services, scenario, false);
        PumpIdle(core, services);
        var before = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var instance = before.LinkGraph.SourceComponentInstances.Single(item => item.Id == fixture.TemplateInstanceId);
        var sourceIds = before.LinkGraph.Nodes.Where(node => instance.SourceNodeIds.Contains(node.Id))
            .Select(node => node.ObjectId).ToList();

        var partial = scenario.Contains("partial", StringComparison.Ordinal);
        var manual = scenario.EndsWith("manual", StringComparison.Ordinal);
        var addedId = AddInputComponentFixtureAddition(doc, services, fixture, scenario, true, false);
        var addedIds = new List<Guid> { addedId };
        if (partial)
            addedIds.Add(AddInputComponentFixtureAddition(doc, services, fixture, scenario, true, false));
        foreach (var id in addedIds)
        {
            using var mutation = AssemblyLinkMutationGate.Enter();
            using var attributes = doc.Objects.FindId(id).Attributes.Duplicate();
            foreach (var key in MaterialAssignment.MetadataKeys)
                attributes.SetUserString(key, null);
            Require(doc.Objects.ModifyAttributes(id, attributes, true), "The matching-shape addition must begin without material metadata.");
        }
        if (partial)
        {
            using var mutation = AssemblyLinkMutationGate.Enter();
            addedIds[1] = doc.Objects.Transform(addedIds[1], Transform.Translation(20, 0, 0), true);
            Require(addedIds[1] != Guid.Empty, "The second unassigned addition must occupy a different position.");
        }
        sourceIds.AddRange(addedIds);
        Require(doc.Groups.Delete(FindComponentFixtureGroup(doc, instance.SourceGroupId)), "The component must be ungrouped before adding the TBD member.");
        new UtilityGeometryService(services.Layers).Regroup(doc, sourceIds);
        PumpIdle(core, services);
        Require(services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!.PendingComponentUpdates.Single()
                .AddedObjectIds.ToHashSet().SetEquals(addedIds),
            "Native regrouping must stage the unassigned addition before Update Assembly.");
        services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
        PumpIdle(core, services);

        var added = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var addedSource = added.LinkGraph.Nodes.Single(node => node.ObjectId == addedId && node.Role == AssemblyLinkRoles.Source);
        var tbdPart = added.Parts.Single(part => part.Id == addedSource.PartId);
        Require(tbdPart.Id != fixture.FirstPartId && tbdPart.Quantity == addedIds.Count &&
                string.IsNullOrWhiteSpace(tbdPart.CategorizationMaterialId) && added.Parts.Count == 4,
            "Shape-identical unassigned additions must initially share their own TBD category.");
        var tbdFlat = added.LinkGraph.Nodes.Single(node => node.PartId == tbdPart.Id && node.Role == AssemblyLinkRoles.FlatPart);
        if (partial)
        {
            var ancestor = tbdFlat;
            var visited = new HashSet<Guid>();
            while (ancestor.Role != AssemblyLinkRoles.Source && visited.Add(ancestor.Id))
            {
                var parentId = added.LinkGraph.Edges.Single(edge => edge.ChildNodeId == ancestor.Id).ParentNodeId;
                ancestor = added.LinkGraph.Nodes.Single(node => node.Id == parentId);
            }
            Require(ancestor.Role == AssemblyLinkRoles.Source && addedIds.Contains(ancestor.ObjectId),
                "The TBD flat's retained recipe must originate at one of the two unassigned additions.");
            // Exercise both representative branches deterministically. If the edited
            // occurrence owns this flat, that flat follows it into P01 and becomes a
            // duplicate; a new correctly linked TBD flat represents the untouched part.
            addedId = manual ? addedIds.Single(id => id != ancestor.ObjectId) : ancestor.ObjectId;
            addedSource = added.LinkGraph.Nodes.Single(node => node.ObjectId == addedId && node.Role == AssemblyLinkRoles.Source);
        }
        var addedOriginal = added.LinkGraph.Nodes.Single(node => node.Id == added.LinkGraph.Edges.Single(edge =>
            edge.ParentNodeId == addedSource.Id && added.LinkGraph.Nodes.Any(child => child.Id == edge.ChildNodeId &&
                child.Role == AssemblyLinkRoles.OriginalAssembly)).ChildNodeId);
        var retiredFlatId = tbdFlat.ObjectId;
        var keepsTbdFlat = partial && manual;
        var existingFlatId = added.LinkGraph.Nodes.Single(node => node.PartId == fixture.FirstPartId && node.Role == AssemblyLinkRoles.FlatPart).ObjectId;
        var originalSourceLayer = doc.Objects.FindId(addedId).Attributes.LayerIndex;
        var retainedBounds = added.LinkGraph.Nodes.Where(node => keepsTbdFlat || node.ObjectId != retiredFlatId).ToDictionary(node => node.Id,
            node => doc.Objects.FindId(node.ObjectId).Geometry.GetBoundingBox(true));
        var oldManagedPaths = added.LinkGraph.Nodes.Where(node => node.PartId == tbdPart.Id && node.Role != AssemblyLinkRoles.Source)
            .Select(node => doc.Layers[doc.Objects.FindId(node.ObjectId).Attributes.LayerIndex].FullPath).ToArray();
        var existingColor = System.Drawing.Color.FromArgb(17, 101, 173);
        using (AssemblyLinkMutationGate.Enter())
        {
            var store = services.Repository.Load(doc);
            var category = store.FindAssembly(fixture.AssemblyName)!.Parts.Single(part => part.Id == fixture.FirstPartId);
            category.LayerColorArgb = existingColor.ToArgb();
            foreach (var layer in doc.Layers.Where(layer => !layer.IsDeleted &&
                (layer.FullPath.EndsWith("::P01", StringComparison.Ordinal) || layer.FullPath.Contains("::P01::", StringComparison.Ordinal))))
                layer.Color = existingColor;
            services.Repository.Save(doc, store);
        }

        if (scenario.EndsWith("repair-cycle", StringComparison.Ordinal))
        {
            // Model a document already saved by the faulty implementation: both categories
            // now claim BIRCH as their accepted material even though their shapes match.
            // A deliberate object-level material edit can safely re-enter reconciliation.
            using (AssemblyLinkMutationGate.Enter())
            {
                var store = services.Repository.Load(doc);
                var polluted = store.FindAssembly(fixture.AssemblyName)!;
                var pollutedPart = polluted.Parts.Single(part => part.Id == tbdPart.Id);
                pollutedPart.MaterialId = "BIRCH";
                pollutedPart.CategorizationMaterialId = "BIRCH";
                foreach (var node in polluted.LinkGraph.Nodes.Where(node => node.PartId == tbdPart.Id))
                {
                    using var attributes = doc.Objects.FindId(node.ObjectId).Attributes.Duplicate();
                    MaterialAssignment.Set(attributes, new MaterialDefinitionRecord { Id = "BIRCH", Name = "Birch plywood" });
                    Require(doc.Objects.ModifyAttributes(node.ObjectId, attributes, true), "The already-affected fixture must carry matching material on both distinct categories.");
                }
                services.Repository.Save(doc, store);
            }
            using (var attributes = doc.Objects.FindId(addedId).Attributes.Duplicate())
            {
                MaterialAssignment.Set(attributes, new MaterialDefinitionRecord { Id = "ALUMINUM", Name = "Aluminum" });
                Require(doc.Objects.ModifyAttributes(addedId, attributes, true), "Recovery must begin with an explicit different material assignment.");
            }
            PumpIdle(core, services);
            var intermediate = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            var temporary = intermediate.LinkGraph.Nodes.Single(node => node.ObjectId == addedId);
            Require(intermediate.Parts.Single(part => part.Id == temporary.PartId).CategorizationMaterialId == "ALUMINUM" &&
                    intermediate.Parts.Single(part => part.Id == fixture.FirstPartId).Quantity == 2,
                "The temporary material edit must recategorize only the selected occurrence before restoring its intended material.");
        }

        var editOriginal = scenario.Contains("original", StringComparison.Ordinal);
        services.AutomaticallyPropagate = !manual;
        if (scenario.Contains("part-assignment", StringComparison.Ordinal))
        {
            // Exercise the actual part-level service, including its immediate PartRecord
            // metadata save, without constructing/registering Gazelle or touching user settings.
            var stockId = scenario.Contains("stock", StringComparison.Ordinal) ? "BIRCH-STOCK-48-96-1" : "BIRCH";
            var settings = new PluginSettingsRecord
            {
                Materials = new List<MaterialDefinitionRecord>
                {
                    new()
                    {
                        Id = "BIRCH", Name = "Birch plywood", Shapes = new List<MaterialShapeRecord>
                        {
                            new() { Id = stockId, Name = "48 x 96 x 1", Thickness = 1, SheetWidth = 48, SheetHeight = 96 }
                        }
                    }
                }
            };
            var materials = new MaterialLibraryService(services.Repository, new PluginSettingsService(),
                new DocumentActionHistorySink(services.Repository), () => settings);
            materials.AssignMaterialToPart(doc, fixture.AssemblyName, tbdPart.Name, stockId);
            Require(MaterialAssignment.GetMaterialId(doc.Objects.FindId(addedId).Attributes) == stockId,
                "The part-level service must assign the selected stock shape to the source object.");
        }
        else
        {
            using var attributes = doc.Objects.FindId(editOriginal ? addedOriginal.ObjectId : addedId).Attributes.Duplicate();
            MaterialAssignment.Set(attributes, new MaterialDefinitionRecord { Id = "BIRCH", Name = "Birch plywood" });
            Require(doc.Objects.ModifyAttributes(editOriginal ? addedOriginal.ObjectId : addedId, attributes, true),
                "A material-only edit must assign the existing category's material without replacing geometry.");
        }
        if (manual)
        {
            var pendingStore = services.Repository.Load(doc);
            Require(pendingStore.FindAssembly(fixture.AssemblyName)!.Parts.Single(part => part.Id == tbdPart.Id).CategorizationMaterialId == string.Empty,
                "Reading metadata after a source edit must preserve the explicitly unassigned last-accepted category baseline.");
            services.Repository.Save(doc, pendingStore);
            Require(services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!.Parts.Single(part => part.Id == tbdPart.Id).CategorizationMaterialId == string.Empty,
                "Saving and reloading a deferred edit must not silently replace the accepted TBD baseline with live material metadata.");
        }
        PumpIdle(core, services);
        if (manual)
        {
            var waiting = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
            Require(waiting.LinkGraph.Nodes.Single(node => node.Id == addedSource.Id).PartId == tbdPart.Id && waiting.Parts.Count == 4,
                "Paused propagation must retain the separate category until Update Assembly.");
            services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
            PumpIdle(core, services);
        }

        var after = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        var revisedSource = after.LinkGraph.Nodes.Single(node => node.Id == addedSource.Id);
        Require(revisedSource.PartId == fixture.FirstPartId && after.Parts.Count == (partial ? 4 : 3) &&
                (partial ? after.Parts.Single(part => part.Id == tbdPart.Id).Quantity == 1 : after.Parts.All(part => part.Id != tbdPart.Id)),
            "Assigning the matching material to a regroup-added TBD part must merge it into the existing shape/material category.");
        var matchingPart = after.Parts.Single(part => part.Id == fixture.FirstPartId);
        Require(matchingPart.Name == "P01" && matchingPart.Quantity == 3 && matchingPart.CategorizationMaterialId == "BIRCH" &&
                after.Parts.Sum(part => part.Quantity) == 6 + addedIds.Count,
            "The existing part number must survive and its quantity must include the added occurrence exactly once.");
        var updatedInstance = after.LinkGraph.SourceComponentInstances.Single(item => item.Id == fixture.TemplateInstanceId);
        var updatedComponent = after.Components.Single(component => component.Id == updatedInstance.ComponentId);
        Require(updatedComponent.PartQuantities["P01"] == 2 &&
                (partial ? updatedComponent.PartQuantities[tbdPart.Name] == 1 :
                    !updatedComponent.PartNames.Contains(tbdPart.Name) && !updatedComponent.PartQuantities.ContainsKey(tbdPart.Name)),
            "The edited component's recipe must contain two P01s and retain only genuinely unassigned TBD members.");
        if (partial)
        {
            var unchanged = after.LinkGraph.Nodes.Single(node => node.ObjectId == addedIds.Single(id => id != addedId));
            Require(unchanged.PartId == tbdPart.Id && string.IsNullOrEmpty(MaterialAssignment.GetCategorizationMaterialId(doc.Objects.FindId(unchanged.ObjectId).Attributes)) &&
                    string.IsNullOrEmpty(after.Parts.Single(part => part.Id == tbdPart.Id).CategorizationMaterialId),
                "Changing one occurrence's material must not assign material to its untouched TBD sibling or erase its category baseline.");
        }
        Require(doc.Objects.FindId(addedId).Attributes.LayerIndex == originalSourceLayer,
            "Material recategorization must leave the operator's input layer unchanged.");
        foreach (var (nodeId, bounds) in retainedBounds)
        {
            var current = after.LinkGraph.Nodes.SingleOrDefault(node => node.Id == nodeId);
            var previous = added.LinkGraph.Nodes.Single(node => node.Id == nodeId);
            Require(current is not null,
                $"Retained node {nodeId} ({previous.Role}, object {previous.ObjectId}, part {previous.PartId}, TBD flat {retiredFlatId}) must survive.");
            var currentBounds = doc.Objects.FindId(current!.ObjectId).Geometry.GetBoundingBox(true);
            Require(currentBounds.Min.DistanceTo(bounds.Min) < 0.001 && currentBounds.Max.DistanceTo(bounds.Max) < 0.001,
                "A material-only category merge must preserve every retained input, original, drawing and flat placement.");
        }
        foreach (var node in after.LinkGraph.Nodes.Where(node => node.PartId == fixture.FirstPartId))
        {
            var obj = doc.Objects.FindId(node.ObjectId);
            Require(MaterialAssignment.GetCategorizationMaterialId(obj.Attributes) == "BIRCH", "All linked matching-part objects must carry the assigned material.");
            if (node.Role == AssemblyLinkRoles.Source)
                continue;
            var layer = doc.Layers[obj.Attributes.LayerIndex];
            Require((layer.FullPath.EndsWith("::P01", StringComparison.Ordinal) || layer.FullPath.Contains("::P01::", StringComparison.Ordinal)) &&
                    layer.Color.ToArgb() == existingColor.ToArgb(),
                "Every generated stage must use the matching part's existing layer name and color.");
        }
        if (!partial)
            Require(oldManagedPaths.All(path => !doc.Layers.Any(layer => !layer.IsDeleted && layer.FullPath == path)),
                "Empty generated TBD layers must retire after their category merges.");
        Require((doc.Objects.FindId(retiredFlatId) != null) == keepsTbdFlat && doc.Objects.FindId(existingFlatId) != null,
            "The original TBD flat must survive when its source remains TBD, retire if it merges into an existing flat category, and never replace the existing matching flat.");
        AssertSynchronizedFlatOutputs(doc, services, after);
        Require(!after.LinkGraph.Conflicts.Any(conflict => conflict.Status == AssemblyLinkStatuses.Open),
            "A supported matching-material edit must finish without unresolved link issues.");
        services.LinkEvents.UpdateAssembly(doc, fixture.AssemblyName);
        PumpIdle(core, services);
        var repeated = services.Repository.Load(doc).FindAssembly(fixture.AssemblyName)!;
        Require(repeated.Parts.Count == (partial ? 4 : 3) && repeated.Parts.Single(part => part.Id == fixture.FirstPartId).Quantity == 3,
            "Repeating Update Assembly after the merge must not split the category again or double-count its quantity.");
    }
}
