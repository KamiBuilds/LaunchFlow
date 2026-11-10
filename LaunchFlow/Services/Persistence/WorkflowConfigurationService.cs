using LaunchFlow.Models;

namespace LaunchFlow.Services;

/// <summary>
/// Validates the complete website configuration before invoking durable
/// persistence. No WPF controls, vault singleton, browser process or registry
/// dependency is needed to exercise these business rules in tests.
/// </summary>
public sealed class WorkflowConfigurationService
{
    private readonly Action<Configuration> _persist;
    private readonly Action<string> _validateUrl;

    public WorkflowConfigurationService(Action<Configuration> persist, Action<string> validateUrl)
    {
        _persist = persist ?? throw new ArgumentNullException(nameof(persist));
        _validateUrl = validateUrl ?? throw new ArgumentNullException(nameof(validateUrl));
    }

    /// <summary>
    /// Either validates and durably persists the entire snapshot or throws.
    /// The caller must not signal "saved" on a failed validation or write.
    /// This service never touches credential-editor values.
    /// </summary>
    public void Save(Configuration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Validate(configuration);
        _persist(configuration);
    }

    public void Validate(Configuration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configuration.Workflows);
        foreach (var workflow in configuration.Workflows)
        {
            if (workflow is null)
                throw new ArgumentException("Website configuration contains a null workflow.");
            _validateUrl(workflow.StartUrl);
            if (!string.IsNullOrWhiteSpace(workflow.DestinationUrl))
                _validateUrl(workflow.DestinationUrl);
            ValidateTrustedOrigin(workflow.CredentialOrigin, "Trusted login origin");
            ValidateTrustedOrigin(workflow.PasswordCredentialOrigin, "Trusted password page");
        }
    }

    private void ValidateTrustedOrigin(string? raw, string description)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        _validateUrl(raw);
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(description + " must use HTTPS.");
    }
}
