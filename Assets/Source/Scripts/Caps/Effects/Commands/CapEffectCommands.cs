using System.Collections.Generic;

public interface ICapEffectCommand
{
}

public interface ICapEffectCommandSink
{
    void Add(ICapEffectCommand command);
}

internal sealed class CapEffectCommandBuffer : ICapEffectCommandSink
{
    private readonly List<ICapEffectCommand> _commands = new();

    public IReadOnlyList<ICapEffectCommand> Commands => _commands;

    public void Add(ICapEffectCommand command)
    {
        if (command != null)
            _commands.Add(command);
    }

    public void Clear() => _commands.Clear();
}
