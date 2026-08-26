using System.Text;
using Microsoft.EntityFrameworkCore;

namespace PandaPocket.Services.Invoice.Persistence;

/// <summary>
/// Renames every column, key and index to snake_case.
///
/// EF Core defaults to the .NET property name, so <c>KeyPrefix</c> becomes a
/// column called "KeyPrefix", which in Postgres is case-sensitive and has to be
/// double-quoted in every hand-written query. That is unpleasant to work with in
/// psql or pgAdmin, and it makes the live schema disagree with the data model
/// documented in the report.
///
/// Applied at the end of OnModelCreating so explicit configuration still wins.
/// </summary>
public static class SnakeCaseNaming
{
    public static void ApplySnakeCaseNames(this ModelBuilder builder)
    {
        foreach (var entity in builder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.GetColumnName()));
            }

            foreach (var key in entity.GetKeys())
            {
                key.SetName(ToSnakeCase(key.GetName()));
            }

            foreach (var fk in entity.GetForeignKeys())
            {
                fk.SetConstraintName(ToSnakeCase(fk.GetConstraintName()));
            }

            foreach (var index in entity.GetIndexes())
            {
                // Indexes given an explicit name above are already snake_case;
                // ToSnakeCase leaves those unchanged.
                index.SetDatabaseName(ToSnakeCase(index.GetDatabaseName()));
            }
        }
    }

    private static string? ToSnakeCase(string? name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        var builder = new StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];

            if (char.IsUpper(c))
            {
                // Underscore before an uppercase run only when it starts a new
                // word, so "InvoiceId" becomes invoice_id rather than i_nvoice_id
                // and "IX_Foo" is not mangled into i_x__foo.
                var previous = i > 0 ? name[i - 1] : '\0';
                var isBoundary = i > 0 && previous != '_' &&
                                 (!char.IsUpper(previous) || (i + 1 < name.Length && char.IsLower(name[i + 1])));

                if (isBoundary) builder.Append('_');
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
