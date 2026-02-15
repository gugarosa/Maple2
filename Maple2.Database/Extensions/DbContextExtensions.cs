using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Maple2.Database.Extensions;

public static class DbContextExtensions {
    public static string? GetTableName<T>(this DbContext context) where T : class {
        IEntityType entityType = context.Model.GetEntityTypes().First(type => type.ClrType == typeof(T));

        IAnnotation tableNameAnnotation = entityType.GetAnnotation("Relational:TableName");
        return tableNameAnnotation.Value?.ToString();
    }

    public static bool TrySaveChanges(this DbContext context, bool autoAccept = true) {
        // Best-effort save with lightweight concurrency resolution.
        // If a DbUpdateConcurrencyException occurs (due to rowversion columns),
        // refresh originals from database and retry to prefer last-write-wins for this context.
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++) {
            try {
                context.SaveChanges(autoAccept);
                return true;
            } catch (DbUpdateConcurrencyException ex) {
                if (attempt == maxAttempts) {
                    Console.WriteLine($"> Failed {context.ContextId}");
                    Console.WriteLine(ex);
                    return false;
                }

                foreach (var entry in ex.Entries) {
                    var databaseValues = entry.GetDatabaseValues();
                    if (databaseValues == null) {
                        // Row was deleted; detach to avoid looping and let next attempt proceed
                        entry.State = EntityState.Detached;
                        continue;
                    }
                    // Update original values to current DB to clear the conflict,
                    // keeping current values as the desired state to write.
                    entry.OriginalValues.SetValues(databaseValues);
                }
                // Retry loop
            } catch (Exception ex) {
                Console.WriteLine($"> Failed {context.ContextId}");
                Console.WriteLine(ex);
                return false;
            }
        }
        return false;
    }

    internal static void DisplayStates(this IEnumerable<EntityEntry> entries) {
        foreach (EntityEntry entry in entries) {
            Console.WriteLine($"Entity: {entry.Entity.GetType().Name}, State: {entry.State.ToString()} ");
        }
    }

    public static void Overwrite<T>(this DbContext context, T entity) where T : class {
        context.Entry(entity).State = EntityState.Modified;
    }
}
