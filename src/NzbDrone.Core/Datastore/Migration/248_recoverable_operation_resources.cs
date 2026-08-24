using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(248)]
    public class recoverable_operation_resources : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Create.Table("MovieEditionFileOperationResources")
                .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                .WithColumn("OperationId").AsInt32().NotNullable()
                    .ForeignKey("FK_MovieEditionFileOperationResources_OperationId", "MovieEditionFileOperations", "Id")
                    .OnDelete(Rule.Cascade)
                .WithColumn("ResourceKey").AsString(512).NotNullable();

            Create.Index("UX_MovieEditionFileOperationResources_ResourceKey")
                .OnTable("MovieEditionFileOperationResources")
                .OnColumn("ResourceKey").Ascending()
                .WithOptions().Unique();

            Create.Index("IX_MovieEditionFileOperationResources_OperationId_ResourceKey")
                .OnTable("MovieEditionFileOperationResources")
                .OnColumn("OperationId").Ascending()
                .OnColumn("ResourceKey").Ascending();

            Execute.WithConnection(BackfillResources);
        }

        private void BackfillResources(IDbConnection connection, IDbTransaction transaction)
        {
            var activeResources = connection.Query<ActiveResource248>(@"SELECT ""Id"", ""ActiveResourceKey"" FROM ""MovieEditionFileOperations"" WHERE ""ActiveResourceKey"" IS NOT NULL", transaction: transaction)
                                            .Where(resource => !string.IsNullOrWhiteSpace(resource.ActiveResourceKey))
                                            .Select(resource => new ActiveResource248 { Id = resource.Id, ActiveResourceKey = resource.ActiveResourceKey.Trim() })
                                            .GroupBy(resource => resource.ActiveResourceKey, StringComparer.Ordinal)
                                            .OrderBy(group => group.Key, StringComparer.Ordinal)
                                            .ToList();

            foreach (var group in activeResources)
            {
                var operations = group.OrderBy(resource => resource.Id).ToList();
                if (operations.Count > 1)
                {
                    connection.Execute(@"UPDATE ""MovieEditionFileOperations"" SET ""State""=@state, ""LastError""=@error WHERE ""Id"" IN @ids",
                                       new
                                       {
                                           state = 10,
                                           error = "Migration 248 quarantined normalized active-resource collisions for operator recovery.",
                                           ids = operations.Select(resource => resource.Id).ToArray()
                                       },
                                       transaction);
                }

                connection.Execute(@"INSERT INTO ""MovieEditionFileOperationResources"" (""OperationId"", ""ResourceKey"") VALUES (@operationId, @resourceKey)",
                                   new { operationId = operations[0].Id, resourceKey = group.Key },
                                   transaction);
            }
        }

        private sealed class ActiveResource248
        {
            public int Id { get; set; }
            public string ActiveResourceKey { get; set; }
        }
    }
}
