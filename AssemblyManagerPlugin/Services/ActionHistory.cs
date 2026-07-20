using AssemblyManagerPlugin.Core;
using Rhino;

namespace AssemblyManagerPlugin.Services;

public interface IActionHistorySink
{
    void Record(RhinoDoc doc, ActionHistoryEntry entry);
}

public sealed class DocumentActionHistorySink : IActionHistorySink
{
    private readonly AssemblyRepository _repository;

    public DocumentActionHistorySink(AssemblyRepository repository)
    {
        _repository = repository;
    }

    public void Record(RhinoDoc doc, ActionHistoryEntry entry)
    {
        var store = _repository.Load(doc);
        store.ActionHistory.Add(entry);
        _repository.Save(doc, store);
    }
}

public interface IRemoteActionHistorySink : IActionHistorySink
{
}

public sealed class DisabledRemoteActionHistorySink : IRemoteActionHistorySink
{
    public void Record(RhinoDoc doc, ActionHistoryEntry entry)
    {
        // Future remote logging plugs in here. Keeping this interface from day one
        // prevents command code from depending on document storage.
    }
}
