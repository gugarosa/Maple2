namespace Maple2.Model.Metadata;

public record ConstantsTable(IReadOnlyDictionary<string, string> Entries) : Table;

public record ServerConstantsTable(IReadOnlyDictionary<string, string> Entries) : ServerTable;
