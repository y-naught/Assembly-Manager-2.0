using AssemblyManagerPlugin.Core;
using AssemblyManagerPlugin.Services;
using Rhino;
using Rhino.Runtime.InProcess;

internal static partial class Program
{
    private static void RunUpdateFeedbackScenario(RhinoCore core, RegressionServices services, string scenario, List<RhinoDoc> fixtureDocs)
    {
        var doc = RhinoDoc.Create(null);
        fixtureDocs.Add(doc);
        doc.ModelUnitSystem = UnitSystem.Inches;
        doc.ModelAbsoluteTolerance = 0.001;
        services.AutomaticallyPropagate = true;
        var hasFlats = scenario.EndsWith("flat", StringComparison.Ordinal);
        var assembly = CreateAssemblyUpdateFixture(doc, services, scenario, hasFlats, false, false, false);
        PumpIdle(core, services);
        services.UpdateFeedback.Clear();

        if (scenario == "update-feedback-noop")
        {
            Require(services.ReferenceUpdates.RefreshDescendantsFromSources(doc, Array.Empty<Guid>()) == 0,
                "An empty automatic update request must not refresh geometry.");
            Require(services.ReferenceUpdates.RefreshDescendantsFromSources(doc, new[] { Guid.Empty, Guid.Empty }) == 0,
                "Empty UUIDs must not start an automatic update.");
            Require(services.ReferenceUpdates.RefreshDescendantsFromSources(doc, new[] { Guid.NewGuid() }) == 0,
                "An object outside all assembly source graphs must not start an update.");
            Require(services.UpdateFeedback.Count == 0, "No-op automatic requests must not print update progress or completion messages.");
            return;
        }

        var sources = assembly.LinkGraph.Nodes.Where(node => node.Role == AssemblyLinkRoles.Source).ToArray();
        if (scenario == "update-feedback-failure")
        {
            var failingUpdates = new ReferenceUpdateService(services.Repository, new FailingFeedbackHistorySink(), services.Lineage,
                services.Fingerprints, new AssemblyCategorizationReconciliationService(services.Fingerprints, services.Layers, services.Lineage),
                updateFeedback: services.UpdateFeedback.Add);
            var sawExpectedFailure = false;
            using (AssemblyLinkMutationGate.Enter())
            {
                ReplaceUpdateFixtureBox(doc, sources[0].ObjectId, 12);
                try
                {
                    failingUpdates.RefreshAssemblyReferences(doc, assembly.Name);
                }
                catch (InvalidOperationException exception) when (exception.Message == FailingFeedbackHistorySink.FailureMessage)
                {
                    sawExpectedFailure = true;
                }
            }
            Require(sawExpectedFailure, "An unexpected update failure must still reach the caller.");
            Require(services.UpdateFeedback.Count == 4 && services.UpdateFeedback[0] == $"Gazelle updating assembly '{assembly.Name}'..." &&
                    services.UpdateFeedback[^1].StartsWith("Gazelle assembly update stopped before completion after ", StringComparison.Ordinal) &&
                    services.UpdateFeedback[^1].Contains("See the error or Link Issues in Assembly Manager.", StringComparison.Ordinal),
                "An unexpected failure must terminate progress with a clear interrupted-update message and review guidance.");
            Require(services.UpdateFeedback.All(message => !message.Contains("assembly update finished", StringComparison.Ordinal)),
                "An interrupted update must not claim successful completion, even if earlier geometry work was saved.");
            return;
        }
        if (scenario == "update-feedback-auto-batch-flat")
        {
            // Both native replacement events arrive before the same idle pass. Progress
            // should describe the assembly stages once, not repeat for each changed part.
            for (var index = 0; index < sources.Length; index++)
                ReplaceUpdateFixtureBox(doc, sources[index].ObjectId, 12 + 2 * index);
            PumpIdle(core, services);
            AssertUpdateFeedbackStages(services.UpdateFeedback, assembly.Name,
                $"Gazelle updating linked assemblies from {sources.Length} changed source object(s)...", hasFlats: true);
            var after = services.Repository.Load(doc).FindAssembly(assembly.Name)!;
            AssertSynchronizedFlatOutputs(doc, services, after);
            Require(services.UpdateFeedback[^1].Contains("No open link issues in the document.", StringComparison.Ordinal),
                "A clean automatic update should report that no document link issues remain.");
            return;
        }

        if (scenario == "update-feedback-document-issues")
        {
            // This unrelated issue predates the selected assembly's update. Completion
            // must report the document total, not only issues created by this operation.
            var store = services.Repository.Load(doc);
            var unrelated = new AssemblyRecord { Name = "Unrelated feedback review fixture" };
            unrelated.LinkGraph.Conflicts.Add(new LinkConflictRecord
            {
                ConflictType = AssemblyLinkConflictTypes.LegacyMigrationIncomplete,
                Message = "Pre-existing unrelated review item for command-line feedback regression."
            });
            store.Assemblies.Add(unrelated);
            services.Repository.Save(doc, store);
        }

        // Suppress fixture-edit events so this specifically exercises the manual service
        // entrypoint, independently of the automatic listeners covered above.
        using (AssemblyLinkMutationGate.Enter())
        {
            ReplaceUpdateFixtureBox(doc, sources[0].ObjectId, 12);
            var refreshed = services.ReferenceUpdates.RefreshAssemblyReferences(doc, assembly.Name);
            Require(refreshed > 0, "A manual update must refresh linked geometry before reporting completion.");
        }
        AssertUpdateFeedbackStages(services.UpdateFeedback, assembly.Name,
            $"Gazelle updating assembly '{assembly.Name}'...", hasFlats);
        var completion = services.UpdateFeedback[^1];
        Require(completion.Contains("1 new part category(s).", StringComparison.Ordinal),
            "The manual completion summary must report the newly split category.");
        if (scenario == "update-feedback-document-issues")
        {
            Require(completion.Contains("1 open link issue(s) in the document", StringComparison.Ordinal) &&
                    completion.Contains("Link Issues at the bottom of Assembly Manager", StringComparison.Ordinal) &&
                    !completion.Contains("No open link issues", StringComparison.Ordinal),
                "Completion must expose pre-existing document-wide issues and point to the relocated review panel.");
        }
        else
        {
            AssertSynchronizedFlatOutputs(doc, services, services.Repository.Load(doc).FindAssembly(assembly.Name)!);
            Require(completion.Contains("No open link issues in the document.", StringComparison.Ordinal),
                "A clean manual update should report that no document link issues remain.");
        }
    }

    private static void AssertUpdateFeedbackStages(IReadOnlyList<string> messages, string assemblyName, string startMessage, bool hasFlats)
    {
        var expectedStages = new List<string>
        {
            startMessage,
            $"Gazelle updating linked geometry for '{assemblyName}'...",
            $"Gazelle recategorizing parts and updating quantities/materials for '{assemblyName}'..."
        };
        if (hasFlats)
            expectedStages.Add($"Gazelle updating flat PARTS geometry, placements, and labels for '{assemblyName}'...");
        Require(messages.Count == expectedStages.Count + 1,
            $"Each update must report one start, one message per applicable assembly stage, and one completion; got [{string.Join(" | ", messages)}].");
        Require(messages.Take(expectedStages.Count).SequenceEqual(expectedStages),
            "Update feedback must follow actual execution order: start, linked geometry, categorization, then existing flat output maintenance.");
        Require(messages[^1].StartsWith("Gazelle assembly update finished in ", StringComparison.Ordinal) &&
                messages[^1].Contains(" s: ", StringComparison.Ordinal) &&
                messages[^1].Contains(" linked object(s) refreshed; ", StringComparison.Ordinal) &&
                messages[^1].Contains(" new part category(s).", StringComparison.Ordinal),
            "Completion must include elapsed time and linked-geometry/category totals without depending on an exact runtime.");
        Require(messages.All(message => !message.Contains("stopped before completion", StringComparison.Ordinal)),
            "A completed update must not also print an interrupted-update message.");
    }

    private sealed class FailingFeedbackHistorySink : IActionHistorySink
    {
        public const string FailureMessage = "Deliberate regression failure while recording update history.";

        public void Record(RhinoDoc doc, ActionHistoryEntry entry) => throw new InvalidOperationException(FailureMessage);
    }
}
