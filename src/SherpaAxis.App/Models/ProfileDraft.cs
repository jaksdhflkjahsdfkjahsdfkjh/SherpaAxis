using SherpaAxis.Profiles;

namespace SherpaAxis.App.Models;

public sealed record ProfileDraft(Guid Id, string Name, string? SavedFileName, Dictionary<PedalAxis, AxisEditor> Editors);
public sealed record ProfileDraftSession(int Schema, IReadOnlyList<ProfileDraft> Profiles);
