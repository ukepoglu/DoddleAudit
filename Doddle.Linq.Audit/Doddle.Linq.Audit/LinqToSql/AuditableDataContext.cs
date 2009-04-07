using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data.Linq;
using System.Reflection;
using System.Data.Linq.Mapping;
using System.Linq.Expressions;

namespace Doddle.Linq.Audit.LinqToSql
{
    public abstract class AuditableDataContext : DataContext, IAuditableContext
    {
        #region Constructors

        protected AuditableDataContext(string fileOrServerOrConnection)
            : base(fileOrServerOrConnection)
        { }
        protected AuditableDataContext(string fileOrServerOrConnection, MappingSource mapping)
            : base(fileOrServerOrConnection, mapping)
        { }
        protected AuditableDataContext(System.Data.IDbConnection connection)
            : base(connection)
        { }
        protected AuditableDataContext(System.Data.IDbConnection connection, MappingSource mapping)
            : base(connection, mapping)
        { }

        #endregion

        private readonly List<AuditedEntity> _queuedRecords = new List<AuditedEntity>();
        private readonly List<IAuditDefinition> _auditDefinitions = new List<IAuditDefinition>();
        private readonly List<Func<MemberInfo, object, bool>> _propertyAuditRules = new List<Func<MemberInfo, object, bool>>();


        /// <summary>
        /// This method defines how to insert the actual audit record into the database
        /// </summary>
        /// <param name="record"></param>
        protected abstract void InsertAuditRecordToDatabase(AuditedEntity record);


        public IList<IAuditDefinition> AuditDefinitions
        {
            get { return _auditDefinitions; }
        }

        public IEnumerable Inserts
        {
            get { return GetChangeSet().Inserts; }
        }

        public IEnumerable Updates
        {
            get { return GetChangeSet().Updates; }
        }

        public IEnumerable Deletes
        {
            get { return GetChangeSet().Deletes; }
        }

        public void InsertAuditRecord(AuditedEntity record)
        {
            _queuedRecords.Add(record);
        }

        public IEnumerable<MemberAudit> GetModifiedFields(object entity)
        {
            ITable table;

            try
            {
                table = GetTable(entity.GetType());
            }
            catch
            {
                table = GetTable(entity.GetType().BaseType);
            }

            return
                table.GetModifiedMembers(entity).Select(
                    mmi =>
                    new MemberAudit(mmi.Member, mmi.CurrentValue, mmi.OriginalValue));
        }

        public PropertyInfo GetEntityPrimaryKey(Type entityType)
        {
            try
            {
                var pk = Mapping.GetTable(entityType).RowType.DataMembers.Single(md => md.IsPrimaryKey);
                return (PropertyInfo)pk.Member;
            }
            catch
            {
                throw new InvalidOperationException(
                    string.Format(
                        "Auditing logic is only capable of processing tables with a single primary key. Please modify this table structure or remove the table '{0}' from automatic auditing.",
                        Mapping.GetTable(entityType).TableName));
            }
        }

        public PropertyInfo GetEntityPrimaryKey<TEntity>()
        {
            return GetEntityPrimaryKey(typeof(TEntity));
        }

        public string GetEntityRelationshipKeyName<T, TR>()
        {
            Type entityType = typeof(T);
            Type relType = typeof(TR);

            return Mapping.GetTable(entityType).RowType.Associations.First(ma => ma.OtherType.Type == relType).OtherKey.First().Name;
        }

        public IList<Func<MemberInfo, object, bool>> PropertyAuditRules
        {
            get { return _propertyAuditRules; }
        }


        /// <summary>
        /// Define all default audits that should be applied to this database
        /// </summary>
        protected virtual void DefaultAuditDefinitions()
        {

        }

        public override void SubmitChanges(ConflictMode failureMode)
        {
            PropertyAuditRules.Add((m, e) => m.HasAttribute(typeof(ColumnAttribute)));
            DefaultAuditDefinitions();
            
            ContextAuditor auditor = new ContextAuditor(this);
            auditor.AuditPendingDataModifications();

            base.SubmitChanges(failureMode);

            foreach (AuditedEntity record in _queuedRecords)
            {
                if (record.Action == AuditAction.Insert)
                {
                    record.UpdateKeys();
                }
                InsertAuditRecordToDatabase(record);
            }

            // Submit all audits to the db, continue processing even if Auditing records fail
            base.SubmitChanges(ConflictMode.ContinueOnConflict);
        }
    }
}
