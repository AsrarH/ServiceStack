﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Funq;
using ServiceStack.Data;
using ServiceStack.OrmLite;
using ServiceStack.Reflection;

namespace ServiceStack
{
    public class AutoQueryFeature : IPlugin
    {
        public HashSet<string> IgnoreProperties { get; set; }
        public int? MaxLimit { get; set; } 
        public string UseNamedConnection { get; set; }
        public bool? EnableSqlFilters { get; set; }
        public Type AutoQueryServiceBaseType { get; set; }

        public AutoQueryFeature()
        {
            IgnoreProperties = new[] { "Skip", "Take" }.ToHashSet();
            AutoQueryServiceBaseType = typeof(AutoQueryServiceBase);
        }

        public void Register(IAppHost appHost)
        {
            appHost.GetContainer().Register<IAutoQuery>(c => 
                new AutoAutoQuery {
                    IgnoreProperties = IgnoreProperties,
                    MaxLimit = MaxLimit,
                    Db = UseNamedConnection != null 
                        ? c.Resolve<IDbConnectionFactory>().OpenDbConnection(UseNamedConnection)
                        : c.Resolve<IDbConnectionFactory>().OpenDbConnection(),
                })
                .ReusedWithin(ReuseScope.None);

            appHost.AfterInitCallbacks.Add(OnAfterLoad);
        }

        void OnAfterLoad(IAppHost appHost)
        {
            var ssHost = (ServiceStackHost) appHost;
            var scannedTypes = ssHost.ServiceController.ResolveServicesFn();
            var misingRequestTypes = scannedTypes
                .Where(x => x.HasInterface(typeof(IQuery)))
                .Where(x => !appHost.Metadata.OperationsMap.ContainsKey(x))
                .ToList();

            if (misingRequestTypes.Count == 0)
                return;

            var serviceType = GenerateMissingServices(misingRequestTypes);
            appHost.RegisterService(serviceType);
        }

        Type GenerateMissingServices(IEnumerable<Type> misingRequestTypes)
        {
            var assemblyName = new AssemblyName { Name = "tmpAssembly" };
            var typeBuilder =
                Thread.GetDomain().DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run)
                .DefineDynamicModule("tmpModule")
                .DefineType("__AutoQueryServices", 
                    TypeAttributes.Public | TypeAttributes.Class, 
                    AutoQueryServiceBaseType);

            var emptyCtor = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
            var baseType = AutoQueryServiceBaseType.BaseType;
            if (baseType != null)
            {
                var ctorIl = emptyCtor.GetILGenerator();
                ctorIl.Emit(OpCodes.Ldarg_0);
                ctorIl.Emit(OpCodes.Call, baseType.GetEmptyConstructor());
                ctorIl.Emit(OpCodes.Ret);
            }

            foreach (var requestType in misingRequestTypes)
            {
                var method = typeBuilder.DefineMethod("Any", MethodAttributes.Public | MethodAttributes.Virtual,
                    CallingConventions.Standard);

                var genericDef = requestType.GetTypeWithGenericTypeDefinitionOf(typeof(IQuery<,>));
                var hasExplicitInto = genericDef != null;
                if (genericDef == null)
                    genericDef = requestType.GetTypeWithGenericTypeDefinitionOf(typeof(IQuery<>));

                method.SetParameters(requestType);
                method.SetReturnType(typeof(object));

                var il = method.GetILGenerator();

                var genericArgs = genericDef.GetGenericArguments();
                var mi = AutoQueryServiceBaseType.GetMethods()
                    .First(x => x.GetGenericArguments().Length == genericArgs.Length);
                var genericMi = mi.MakeGenericMethod(genericArgs);

                var queryType = hasExplicitInto
                    ? typeof(IQuery<,>).MakeGenericType(genericArgs)
                    : typeof(IQuery<>).MakeGenericType(genericArgs);

                il.Emit(OpCodes.Nop);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Box, queryType);
                il.Emit(OpCodes.Callvirt, genericMi);
                il.Emit(OpCodes.Ret);
            }

            var servicesType = typeBuilder.CreateType();
            return servicesType;
        }
    }

    public interface IAutoQuery
    {
        SqlExpression<From> CreateQuery<From>(IQuery<From> model, Dictionary<string, string> dynamicParams);

        QueryResponse<From> Execute<From>(IQuery<From> model, SqlExpression<From> query);

        SqlExpression<From> CreateQuery<From, Into>(IQuery<From, Into> model, Dictionary<string, string> dynamicParams);

        QueryResponse<Into> Execute<From, Into>(IQuery<From, Into> model, SqlExpression<From> query);
    }

    public abstract class AutoQueryServiceBase : Service
    {
        public IAutoQuery AutoQuery { get; set; }

        public virtual object Exec<From>(IQuery<From> dto)
        {
            var q = AutoQuery.CreateQuery(dto, Request.GetRequestParams());
            return AutoQuery.Execute(dto, q);
        }

        public virtual object Exec<From, Into>(IQuery<From,Into> dto)
        {
            var q = AutoQuery.CreateQuery(dto, Request.GetRequestParams());
            return AutoQuery.Execute(dto, q);
        }
    }

    public class AutoAutoQuery : IAutoQuery, IDisposable
    {
        public HashSet<string> IgnoreProperties { get; set; }

        public int? MaxLimit { get; set; } 

        public virtual IDbConnection Db { get; set; }

        public virtual void Dispose()
        {
            if (Db != null)
                Db.Dispose();
        }

        private static Dictionary<Type, ITypedQuery> TypedQueries = new Dictionary<Type, ITypedQuery>();

        public static ITypedQuery GetTypedQuery(Type dtoType, Type fromType)
        {
            ITypedQuery defaultValue;
            if (TypedQueries.TryGetValue(dtoType, out defaultValue)) return defaultValue;

            var genericType = typeof(TypedQuery<,>).MakeGenericType(dtoType, fromType);
            defaultValue = genericType.CreateInstance<ITypedQuery>();
            
            Dictionary<Type, ITypedQuery> snapshot, newCache;
            do
            {
                snapshot = TypedQueries;
                newCache = new Dictionary<Type, ITypedQuery>(TypedQueries);
                newCache[dtoType] = defaultValue;

            } while (!ReferenceEquals(
                Interlocked.CompareExchange(ref TypedQueries, newCache, snapshot), snapshot));

            return defaultValue;
        }

        public SqlExpression<From> CreateQuery<From>(IQuery<From> model, Dictionary<string, string> dynamicParams)
        {
            var typedQuery = GetTypedQuery(model.GetType(), typeof (From));
            return (SqlExpression<From>) typedQuery.CreateQuery(Db, model, dynamicParams, IgnoreProperties, MaxLimit);
        }

        public QueryResponse<From> Execute<From>(IQuery<From> model, SqlExpression<From> query)
        {
            var typedQuery = GetTypedQuery(model.GetType(), typeof(From));
            return typedQuery.Execute<From>(Db, query);
        }

        public SqlExpression<From> CreateQuery<From, Into>(IQuery<From, Into> model, Dictionary<string, string> dynamicParams)
        {
            var typedQuery = GetTypedQuery(model.GetType(), typeof(From));
            return (SqlExpression<From>)typedQuery.CreateQuery(Db, model, dynamicParams, IgnoreProperties, MaxLimit);
        }

        public QueryResponse<Into> Execute<From, Into>(IQuery<From, Into> model, SqlExpression<From> query) 
        {
            var typedQuery = GetTypedQuery(model.GetType(), typeof(From));
            return typedQuery.Execute<Into>(Db, query);
        }
    }

    public interface ITypedQuery
    {
        ISqlExpression CreateQuery(
            IDbConnection db,
            IQuery model,
            Dictionary<string, string> dynamicParams,
            HashSet<string> ignoreProperties = null,
            int? maxLimit = null);

        QueryResponse<Into> Execute<Into>(
            IDbConnection db, 
            ISqlExpression query);
    }

    public class TypedQuery<QueryModel, From> : ITypedQuery
    {
        static readonly Dictionary<string, Func<object,object>> PropertyGetters = 
            new Dictionary<string, Func<object, object>>();

        static TypedQuery()
        {
            foreach (var pi in typeof(QueryModel).GetPublicProperties())
            {
                var fn = StaticAccessors.GetValueGetter(typeof(QueryModel), pi);
                PropertyGetters[pi.Name] = fn;
            }
        }

        public ISqlExpression CreateQuery(
            IDbConnection db,
            IQuery model,
            Dictionary<string, string> dynamicParams,
            HashSet<string> ignoreProperties=null,
            int? maxLimit = null)
        {
            var q = db.From<From>();

            if (model is IJoin)
            {
                bool leftJoin = false;
                var dtoInterfaces = model.GetType().GetInterfaces();
                var join = dtoInterfaces.FirstOrDefault(x => x.Name.StartsWith("IJoin`"));
                if (join == null)
                {
                    join = dtoInterfaces.FirstOrDefault(x => x.Name.StartsWith("ILeftJoin`"));
                    if (join == null)
                        throw new ArgumentException("No IJoin<T1,T2,..> interface found");

                    leftJoin = true;
                }

                var joinTypes = join.GetGenericArguments();
                for (var i = 1; i < joinTypes.Length; i++)
                {
                    if (!leftJoin)
                        q.Join(joinTypes[i - 1], joinTypes[i]);
                    else
                        q.LeftJoin(joinTypes[i - 1], joinTypes[i]);
                }
            }

            var take = model.Take ?? maxLimit;
            if (take > maxLimit)
                take = maxLimit;
            q.Limit(model.Skip, take);

            var m = typeof(From).GetModelMetadata();
            foreach (var entry in PropertyGetters)
            {
                var name = entry.Key;
                var matchingField = ignoreProperties == null || !ignoreProperties.Contains(name)
                    ? m.FieldDefinitions.FirstOrDefault(x => x.Name == name)
                    : null;
                if (matchingField == null)
                    continue;

                var quotedColumn = q.DialectProvider.GetQuotedColumnName(m, matchingField);

                var value = entry.Value(model);
                if (value == null)
                    continue;

                q.Where(quotedColumn + " = {0}", value);
            }

            foreach (var entry in dynamicParams)
            {
                var name = entry.Key;
                var matchingField = ignoreProperties == null || !ignoreProperties.Contains(name)
                    ? m.FieldDefinitions.FirstOrDefault(x => x.Name == name)
                    : null;
                if (matchingField == null)
                    continue;

                var quotedColumn = q.DialectProvider.GetQuotedColumnName(m, matchingField);

                var strValue = entry.Value;
                var value = matchingField.FieldType == typeof(string)
                    ? strValue
                    : Convert.ChangeType(strValue, matchingField.FieldType);

                q.Where(quotedColumn + " = {0}", value);
            }

            return q;
        }

        public QueryResponse<Into> Execute<Into>(IDbConnection db, ISqlExpression query)
        {
            var q = (SqlExpression<From>) query;
            var response = new QueryResponse<Into>
            {
                Offset = q.Offset.GetValueOrDefault(0),
                Total = (int)db.Count(q),
                Results = db.Select<Into, From>(q),
            };

            return response;
        }
    }
}