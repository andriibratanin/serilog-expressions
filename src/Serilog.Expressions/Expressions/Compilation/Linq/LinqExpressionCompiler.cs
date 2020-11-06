﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Serilog.Events;
using Serilog.Expressions.Ast;
using Serilog.Expressions.Compilation.Transformations;
using ConstantExpression = Serilog.Expressions.Ast.ConstantExpression;
using Expression = Serilog.Expressions.Ast.Expression;
using ParameterExpression = System.Linq.Expressions.ParameterExpression;
using LX = System.Linq.Expressions.Expression;
using ExpressionBody = System.Linq.Expressions.Expression;

namespace Serilog.Expressions.Compilation.Linq
{
    class LinqExpressionCompiler : SerilogExpressionTransformer<ExpressionBody>
    {
        readonly NameResolver _nameResolver;

        static readonly MethodInfo CollectSequenceElementsMethod = typeof(Intrinsics)
            .GetMethod(nameof(Intrinsics.CollectSequenceElements), BindingFlags.Static | BindingFlags.Public)!;

        static readonly MethodInfo ExtendSequenceValueWithSpreadMethod = typeof(Intrinsics)
            .GetMethod(nameof(Intrinsics.ExtendSequenceValueWithSpread), BindingFlags.Static | BindingFlags.Public)!;

        static readonly MethodInfo ExtendSequenceValueWithItemMethod = typeof(Intrinsics)
            .GetMethod(nameof(Intrinsics.ExtendSequenceValueWithItem), BindingFlags.Static | BindingFlags.Public)!;

        static readonly MethodInfo ConstructSequenceValueMethod = typeof(Intrinsics)
            .GetMethod(nameof(Intrinsics.ConstructSequenceValue), BindingFlags.Static | BindingFlags.Public)!;

        static readonly MethodInfo CollectStructurePropertiesMethod = typeof(Intrinsics)
            .GetMethod(nameof(Intrinsics.CollectStructureProperties), BindingFlags.Static | BindingFlags.Public)!;

        static readonly MethodInfo ConstructStructureValueMethod = typeof(Intrinsics)
            .GetMethod(nameof(Intrinsics.ConstructStructureValue), BindingFlags.Static | BindingFlags.Public)!;

        static readonly MethodInfo CompleteStructureValueMethod = typeof(Intrinsics)
            .GetMethod(nameof(Intrinsics.CompleteStructureValue), BindingFlags.Static | BindingFlags.Public)!;

        static readonly MethodInfo ExtendStructureValueWithSpreadMethod = typeof(Intrinsics)
            .GetMethod(nameof(Intrinsics.ExtendStructureValueWithSpread), BindingFlags.Static | BindingFlags.Public)!;

        static readonly MethodInfo ExtendStructureValueWithPropertyMethod = typeof(Intrinsics)
            .GetMethod(nameof(Intrinsics.ExtendStructureValueWithProperty), BindingFlags.Static | BindingFlags.Public)!;

        static readonly MethodInfo CoerceToScalarBooleanMethod = typeof(Intrinsics)
            .GetMethod(nameof(Intrinsics.CoerceToScalarBoolean), BindingFlags.Static | BindingFlags.Public)!;

        static readonly MethodInfo IndexOfMatchMethod = typeof(Intrinsics)
            .GetMethod(nameof(Intrinsics.IndexOfMatch), BindingFlags.Static | BindingFlags.Public)!;
        
        static readonly MethodInfo TryGetStructurePropertyValueMethod = typeof(Intrinsics)
            .GetMethod(nameof(Intrinsics.TryGetStructurePropertyValue), BindingFlags.Static | BindingFlags.Public)!;

        ParameterExpression Context { get; } = LX.Variable(typeof(LogEvent), "evt");

        LinqExpressionCompiler(NameResolver nameResolver)
        {
            _nameResolver = nameResolver;
        }
        
        public static CompiledExpression Compile(Expression expression, NameResolver nameResolver)
        {
            if (expression == null) throw new ArgumentNullException(nameof(expression));
            var compiler = new LinqExpressionCompiler(nameResolver);
            var body = compiler.Transform(expression); 
            return LX.Lambda<CompiledExpression>(body, compiler.Context).Compile();
        }

        ExpressionBody Splice(Expression<CompiledExpression> lambda)
        {
            return ParameterReplacementVisitor.ReplaceParameters(lambda, Context);
        }
        
        protected override ExpressionBody Transform(CallExpression lx)
        {
            if (!_nameResolver.TryResolveFunctionName(lx.OperatorName, out var m))
                throw new ArgumentException($"The function name `{lx.OperatorName}` was not recognized.");

            var parameterCount = m.GetParameters().Count(pi => pi.ParameterType == typeof(LogEventPropertyValue));
            if (parameterCount != lx.Operands.Length)
                throw new ArgumentException($"The function `{lx.OperatorName}` requires {parameterCount} arguments.");

            var operands = lx.Operands.Select(Transform).ToList();

            // `and` and `or` short-circuit to save execution time; unlike the earlier Serilog.Filters.Expressions, nothing else does.
            if (Operators.SameOperator(lx.OperatorName, Operators.RuntimeOpAnd))
                return CompileLogical(LX.AndAlso, operands[0], operands[1]);

            if (Operators.SameOperator(lx.OperatorName, Operators.RuntimeOpOr))
                return CompileLogical(LX.OrElse, operands[0], operands[1]);

            if (m.GetParameters().Any(pi => pi.ParameterType == typeof(StringComparison)))
                operands.Insert(0, LX.Constant(lx.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

            return LX.Call(m, operands);
        }

        static ExpressionBody CompileLogical(Func<ExpressionBody, ExpressionBody, ExpressionBody> apply, ExpressionBody lhs, ExpressionBody rhs)
        {
            return LX.Convert(
                LX.New(
                    typeof(ScalarValue).GetConstructor(new[]{typeof(object)})!,
                    LX.Convert(apply(
                        LX.Call(CoerceToScalarBooleanMethod, lhs),
                        LX.Call(CoerceToScalarBooleanMethod, rhs)), typeof(object))),
                typeof(LogEventPropertyValue));
        }

        protected override ExpressionBody Transform(AccessorExpression spx)
        {
            var recv = Transform(spx.Receiver);
            return LX.Call(TryGetStructurePropertyValueMethod, LX.Constant(StringComparison.OrdinalIgnoreCase), recv, LX.Constant(spx.MemberName, typeof(string)));
        }
        
        protected override ExpressionBody Transform(ConstantExpression cx)
        {
            return LX.Constant(cx.Constant);
        }

        protected override ExpressionBody Transform(AmbientPropertyExpression px)
        {
            if (px.IsBuiltIn)
            {
                if (px.PropertyName == BuiltInProperty.Level)
                    return Splice(context => new ScalarValue(context.Level));

                if (px.PropertyName == BuiltInProperty.Message)
                    return Splice(context => new ScalarValue(Intrinsics.RenderMessage(context)));

                if (px.PropertyName == BuiltInProperty.Exception)
                    return Splice(context => context.Exception == null ? null : new ScalarValue(context.Exception));

                if (px.PropertyName == BuiltInProperty.Timestamp)
                    return Splice(context => new ScalarValue(context.Timestamp));

                if (px.PropertyName == BuiltInProperty.MessageTemplate)
                    return Splice(context => new ScalarValue(context.MessageTemplate.Text));

                if (px.PropertyName == BuiltInProperty.Properties)
                    return Splice(context => new StructureValue(context.Properties.Select(kvp => new LogEventProperty(kvp.Key, kvp.Value)), null));

                return LX.Constant(null, typeof(LogEventPropertyValue));
            }

            var propertyName = px.PropertyName;
            return Splice(context => Intrinsics.GetPropertyValue(context, propertyName));
        }

        protected override ExpressionBody Transform(Ast.LambdaExpression lmx)
        {
            var parameters = lmx.Parameters.Select(px => Tuple.Create(px, LX.Parameter(typeof(LogEventPropertyValue), px.ParameterName))).ToList();
            var paramSwitcher = new ExpressionConstantMapper(parameters.ToDictionary(px => (object)px.Item1, px => (System.Linq.Expressions.Expression)px.Item2));
            var rewritten = paramSwitcher.Visit(Transform(lmx.Body));

            Type delegateType;
            if (lmx.Parameters.Length == 1)
                delegateType = typeof(Func<LogEventPropertyValue, LogEventPropertyValue>);
            else if (lmx.Parameters.Length == 2)
                delegateType = typeof(Func<LogEventPropertyValue, LogEventPropertyValue, LogEventPropertyValue>);
            else
                throw new NotSupportedException("Unsupported lambda signature.");

            var lambda = LX.Lambda(delegateType, rewritten!, parameters.Select(px => px.Item2).ToArray());
            
            // Unfortunately, right now, functions need to be threaded through in constant scalar values :-D
            return LX.New(typeof(ScalarValue).GetConstructor(new[] {typeof(object)})!,
                LX.Convert(lambda, typeof(object)));
        }

        protected override ExpressionBody Transform(Ast.ParameterExpression prx)
        {
            // Will be within a lambda, which will subsequently sub-in the actual value.
            // The `prx` placeholder needs to be wrapped in a `ScalarValue` so that eager
            // typechecking doesn't fail before we've substituted the real value in.
            return LX.Constant(new ScalarValue(prx), typeof(LogEventPropertyValue));
        }

        protected override ExpressionBody Transform(IndexerWildcardExpression wx)
        {
            return LX.Constant(null, typeof(LogEventPropertyValue));
        }

        protected override ExpressionBody Transform(ArrayExpression ax)
        {
            var elements = new List<ExpressionBody>(ax.Elements.Length);
            var i = 0;
            for (; i < ax.Elements.Length; ++i)
            {
                var element = ax.Elements[i];
                if (element is ItemElement item)
                    elements.Add(Transform(item.Value));
                else
                    break;
            }
            
            var arr = LX.NewArrayInit(typeof(LogEventPropertyValue), elements.ToArray());
            var collected = LX.Call(CollectSequenceElementsMethod, arr);

            for (; i < ax.Elements.Length; ++i)
            {
                var element = ax.Elements[i];
                if (element is ItemElement item)
                    collected = LX.Call(ExtendSequenceValueWithItemMethod, collected, Transform(item.Value));
                else
                {
                    var spread = (SpreadElement) element;
                    collected = LX.Call(ExtendSequenceValueWithSpreadMethod, collected, Transform(spread.Content));
                }
            }
            
            return LX.Call(ConstructSequenceValueMethod, collected);
        }
        
        protected override ExpressionBody Transform(ObjectExpression ox)
        {
            var names = new List<string>();
            var values = new List<ExpressionBody>();

            var i = 0;
            for (; i < ox.Members.Length; ++i)
            {
                var member = ox.Members[i];
                if (member is PropertyMember property)
                {
                    if (names.Contains(property.Name))
                    {
                        var oldPos = names.IndexOf(property.Name);
                        values[oldPos] = Transform(property.Value);
                    }
                    else
                    {
                        names.Add(property.Name);
                        values.Add(Transform(property.Value));
                    }
                }
                else
                {
                    break;
                }
            }
            
            var namesConstant = LX.Constant(names.ToArray(), typeof(string[]));
            var valuesArr = LX.NewArrayInit(typeof(LogEventPropertyValue), values.ToArray());
            var properties = LX.Call(CollectStructurePropertiesMethod, namesConstant, valuesArr);

            if (i == ox.Members.Length)
            {
                // No spreads; more efficient than `Complete*` because erasure is not required.
                return LX.Call(ConstructStructureValueMethod, properties);
            }

            for (; i < ox.Members.Length; ++i)
            {
                var member = ox.Members[i];
                if (member is PropertyMember property)
                {
                    properties = LX.Call(
                        ExtendStructureValueWithPropertyMethod,
                        properties,
                        LX.Constant(property.Name),
                        Transform(property.Value));
                }
                else
                {
                    var spread = (SpreadMember) member;
                    properties = LX.Call(
                        ExtendStructureValueWithSpreadMethod,
                        properties,
                        Transform(spread.Content));
                }
            }

            return LX.Call(CompleteStructureValueMethod, properties);
        }

        protected override ExpressionBody Transform(IndexerExpression ix)
        {
            return Transform(new CallExpression(false, Operators.OpElementAt, ix.Receiver, ix.Index));
        }

        protected override ExpressionBody Transform(IndexOfMatchExpression mx)
        {
            var rx = LX.Constant(mx.Regex);
            var target = Transform(mx.Corpus);
            return LX.Call(IndexOfMatchMethod, target, rx);
        }
    }
}
