namespace CTilde;

/// <summary>Identifies the root project or locked source module that owns a user syntax tree.</summary>
public sealed record SourceOwnerIdentity(
    string ModulePath,
    string? ContentRoot,
    string? SourceIdentityRoot,
    bool IsRootApplication,
    string? LockedRevision)
{
    /// <summary>The compatibility owner assigned to source parsed outside a project or module graph.</summary>
    public static SourceOwnerIdentity ImplicitRoot { get; } = new("<root>", null, null, true, null);
}
