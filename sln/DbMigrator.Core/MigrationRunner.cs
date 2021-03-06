﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Transactions;
using DbMigrator.Core.DataAccess;

namespace DbMigrator.Core
{
    public class MigrationRunner
    {
        private string identifier;
        private readonly IDataProvider dataProvider;
        private readonly IMigrationMapProvider mapProvider;
        private IMigrationRunnerOutputHandler output = new SilentMigratorRunnerOutputHandler();

        public MigrationRunner(
            IDataProvider dataProvider,
            IMigrationMapProvider mapProvider,
            string identifier = null
        ){
            this.dataProvider = dataProvider;
            this.mapProvider = mapProvider;
            this.identifier = identifier;
        }

        public void SetOutputHandler(IMigrationRunnerOutputHandler outputHandler)
        {
            this.output = outputHandler;
        }

        private List<IMigrationFilter> migrationFilters = new List<IMigrationFilter>();
        public void AddFilter(IMigrationFilter filter)
        {
            this.migrationFilters.Add(filter);
        }

        private Action<OnMigratingContext> onMigrating = (x) => { };
        private Action<OnMigrationErrorContext> onMigrationError = (x) => { };
        private Action<OnCompletingTransactionContext> onCompletingTransaction = (x) => { };

        public void SetOnMigrating(Action<OnMigratingContext> action){ this.onMigrating = action; }
        public void SetOnMigrationError(Action<OnMigrationErrorContext> action) { this.onMigrationError = action; }
        public void SetOnCompletingTransaction(Action<OnCompletingTransactionContext> action) { this.onCompletingTransaction = action; }

        public void Migrate()
        {
            output.EventBegin("migration", "Migration");

            //upgrade the schema to be updated
            output.Info("Checking Core Schema");
            dataProvider.UpgradeSchema();
            

            //get nodes
            output.EventBegin("map-load","Migration Map Load");
            var map = mapProvider.GetMigrationMap();
            if (map.Identifier != null)
                this.identifier = map.Identifier;
            if (this.identifier == null)
                throw new InvalidOperationException("Could not define a valid identifier for the migrator.");

            var allMigrationsCount = map.MigrationNodes.SelectMany(x => x.Migrations).Count();
            output.Info(string.Format("{0} migrations found.", allMigrationsCount));
            output.EventEnd("map-load", "Migration Map Load");

            //get nodes current state in the selected database
            output.Info("Checking Migration Map State");
            var databaseMigrationState = GetState(map);

            //filter nodes
            MigrationFilterContext filterContext = new MigrationFilterContext() { MigrationNodes = databaseMigrationState.MigrationNodesInfo };
            output.EventBegin("filtering", "Node Filtering");
            foreach (var migrationFilter in migrationFilters)
            {
                output.Info("Filtering Nodes with filter "+ migrationFilter.GetType().Name);
                migrationFilter.Filter(filterContext);
            }
            output.EventEnd("filtering", "Node Filtering");

            var migrationsToExecuteCount = databaseMigrationState.MigrationNodesInfo.SelectMany(x => x.MigrationsInfo).Where(x => x.CurrentState == Migration.MigrationState.ToUpgrade).Count();
            output.EventBegin("summary", "Migration Plan", migrationsToExecuteCount + " migrations.");
            foreach (var migration in databaseMigrationState.MigrationNodesInfo.SelectMany(x => x.MigrationsInfo).Where(x => x.CurrentState == Migration.MigrationState.ToUpgrade))
            {
                output.Info(string.Format(" - {0}", migration.Migration.Identifier));
            }
            output.EventEnd("summary", "Migration Plan");

            output.EventBegin("transaction", "Transaction");

            bool transactionCommited = false;
            using (var transactionScope = new TransactionScope()
            ){
                //migrate
                bool stopNodeProcessing = false;
                bool hasErrors = false;

                int migratedNodes = 0;
                int markedNodes = 0;

                foreach (var nodeInfo in databaseMigrationState.MigrationNodesInfo)
                {
                    if (stopNodeProcessing)
                        break;

                    var node = nodeInfo.MigrationNode;
                    foreach (var migrationInfo in nodeInfo.MigrationsInfo)
                    {
                        if (stopNodeProcessing)
                            break;

                        if (migrationInfo.CurrentState == Migration.MigrationState.ToUpgrade)
                        {
                            bool jumpCurrentExecution = false;
                            bool markExecution = true;
    

                            var migration = migrationInfo.Migration;
                                
                            var migratingContext = new OnMigratingContext{ Migration = migration  };
                            onMigrating(migratingContext);
                            switch (migratingContext.Decision)
                            {
                                case OnMigratingDecision.Run: { break; }
                                case OnMigratingDecision.Stop: { stopNodeProcessing = true; jumpCurrentExecution = true; markExecution = false; break; }
                                case OnMigratingDecision.Jump: { jumpCurrentExecution = true; markExecution = false; break; }
                                case OnMigratingDecision.JumpAndMark: { jumpCurrentExecution = true; markExecution = true; break; }
                                default: { throw new NotImplementedException(); }
                            }

                            string sql = string.Empty;
                            sql = migration.GetUpgradeSql();

                            if (!jumpCurrentExecution)
                            {
                                try
                                {
                                    output.Info("Executing script " +migration.Identifier);
                                    dataProvider.ExecuteSql(sql);
                                    migratedNodes++;
                                }
                                catch (Exception ex)
                                {
                                    hasErrors = true;
                                    var migrationErrorContext = new OnMigrationErrorContext { Migration = migration, Exception = ex };
                                    onMigrationError(migrationErrorContext);
                                    switch (migrationErrorContext.Decision)
                                    {
                                        case OnMigrationErrorDecision.Stop: { stopNodeProcessing = true; markExecution = false; break; }
                                        case OnMigrationErrorDecision.MarkAnywayAndStop: { stopNodeProcessing = true; break; }
                                        case OnMigrationErrorDecision.Continue: { markExecution = false; break; }
                                        case OnMigrationErrorDecision.MarkAnywayAndContinue: { markExecution = true; break; }
                                        default: { throw new NotImplementedException(); }
                                    }
                                }
                            }

                            if (markExecution)
                            {
                                dataProvider.InsertExecutedMigration(new DataAccess.Entities.ExecutedMigration()
                                {
                                    LastRunScript = sql,
                                    LastRunDate = DateTime.Now,
                                    MigrationId = migration.Identifier,
                                    MigrationNodeId = node.Identifier,
                                    MigrationRunnerId = this.identifier
                                });
                                markedNodes++;
                            }
                        }
                    }
                }
                if (migratedNodes == 0 && markedNodes == 0)
                {
                    output.Info("Nothing to commit or rollback.");
                }
                else
                {
                    var onCompletingTransactionContext = new OnCompletingTransactionContext() { HasErrors = hasErrors };
                    onCompletingTransaction(onCompletingTransactionContext);
                    switch (onCompletingTransactionContext.Decision)
                    {
                        case OnCompletingTransactionDecision.Commit: {
                            output.Info("Commiting...");
                            transactionScope.Complete();
                            transactionCommited = true;
                            //set resolved root
                            break;
                        }
                        case OnCompletingTransactionDecision.Rollback: {
                            output.Info("Rolling back...");
                            break;
                        }
                        default: { throw new NotImplementedException(); }
                    }
                }
            }
            output.EventEnd("transaction", "Transaction");

            foreach (var migrationFilter in migrationFilters)
            {
                migrationFilter.AfterTransaction(transactionCommited);
            }


            output.EventEnd("migration", "Migration");
        }

        public MigrationMapStateInfo GetState(MigrationMap migrationMap)
        {
            var executedMigrations = dataProvider.ListExecutedMigrations(identifier).Select(x => x.MigrationId);
            var migrationMapStateInfo = new MigrationMapStateInfo()
            {
                MigrationNodesInfo = migrationMap.MigrationNodes.Select(x =>{
                    var nodeInfo = new MigrationNodeStateInfo() { MigrationNode=x };
                    nodeInfo.MigrationsInfo = x.Migrations.Select(y =>
                    {
                        var migrationInfo = new MigrationStateInfo() { Migration = y };
                        migrationInfo.CurrentState = executedMigrations.Contains(y.Identifier) ? Migration.MigrationState.Executed : Migration.MigrationState.ToUpgrade;
                        return migrationInfo;
                    }).ToList();
                    return nodeInfo;
                }).ToList()
            };
            return migrationMapStateInfo;
        }

    }
}
