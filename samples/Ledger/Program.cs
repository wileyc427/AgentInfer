using Agentry;

using Ledger;

// The generated LedgerAnalystAgent implements ILedgerAnalyst and takes an
// AgentRunner. In a real host both come from DI; the point of this file is that
// the type exists, the names are what you would guess, and nothing was
// registered by hand.
Console.WriteLine("Generated agent: " + typeof(LedgerAnalystAgent).FullName);
Console.WriteLine("Implements ILedgerAnalyst: " + (typeof(ILedgerAnalyst).IsAssignableFrom(typeof(LedgerAnalystAgent))));

foreach (var method in typeof(LedgerAnalystAgent).GetMethods().Where(m => m.DeclaringType == typeof(LedgerAnalystAgent)))
{
    Console.WriteLine($"  {method.ReturnType.Name} {method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
}

// Constructing it needs an IChatClient, which this sample does not configure —
// running an actual completion is the next commit, not this one.
_ = new Func<AgentRunner, ILedgerAnalyst>(runner => new LedgerAnalystAgent(runner));
