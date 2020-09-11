using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// some things we want to expose to other parts of the engine but to not allow
// algorithms to have access. this certainly isn't the ideal, but we'd like
// to not compile break existing user algorithm's, but instead allow them to
// throw the exception in a backtest and see how to use the new order system.
[assembly: InternalsVisibleTo("QuantConnect.Algorithm.Framework")]
[assembly: InternalsVisibleTo("QuantConnect.Brokerages")]
[assembly: InternalsVisibleTo("QuantConnect.Lean.Engine")]
[assembly: InternalsVisibleTo("QuantConnect.Tests")]
[assembly: AssemblyDescription("QuantConnect LEAN Engine: Common Project - A collection of common definitions and utils")]
