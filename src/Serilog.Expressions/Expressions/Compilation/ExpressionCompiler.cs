﻿using Serilog.Expressions.Ast;
using Serilog.Expressions.Compilation.Arrays;
using Serilog.Expressions.Compilation.Linq;
using Serilog.Expressions.Compilation.Properties;
using Serilog.Expressions.Compilation.Text;
using Serilog.Expressions.Compilation.Variadics;
using Serilog.Expressions.Compilation.Wildcards;

namespace Serilog.Expressions.Compilation
{
    static class ExpressionCompiler
    {
        public static CompiledExpression Compile(Expression expression, NameResolver nameResolver)
        {
            var actual = expression;
            actual = VariadicCallRewriter.Rewrite(actual);
            actual = TextMatchingTransformer.Rewrite(actual);
            actual = LikeSyntaxTransformer.Rewrite(actual);
            actual = PropertiesObjectAccessorTransformer.Rewrite(actual);
            actual = ConstantArrayEvaluator.Evaluate(actual);
            actual = WildcardComprehensionTransformer.Expand(actual);

            return LinqExpressionCompiler.Compile(actual, nameResolver);
        }
    }
}
