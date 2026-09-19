using System;

namespace Jellyfin.Plugin.Chaosflix.Tests.Contract;

/// <summary>
/// A <see cref="FactAttribute"/> that skips unless <see cref="EnvironmentVariable"/> is set.
/// Tests marked with it talk to the real media.ccc.de API, so they must never run as part of
/// <c>run-tests.sh</c>, which gates every push and has to stay offline and fast.
/// </summary>
public sealed class ContractFactAttribute : FactAttribute
{
    /// <summary>Set this to any non-empty value to run the contract tests.</summary>
    public const string EnvironmentVariable = "CCC_CONTRACT";

    /// <summary>
    /// Initializes a new instance of the <see cref="ContractFactAttribute"/> class.
    /// </summary>
    public ContractFactAttribute()
    {
        if (!IsEnabled)
        {
            Skip = $"Contract tests query the real media.ccc.de API; set {EnvironmentVariable}=1 to run them.";
        }
    }

    /// <summary>Gets a value indicating whether the contract suite was opted into.</summary>
    public static bool IsEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable));
}
