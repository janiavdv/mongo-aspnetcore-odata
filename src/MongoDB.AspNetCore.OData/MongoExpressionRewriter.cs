// Copyright 2023-present MongoDB Inc.
//
// Licensed under the Apache License, Version 2.0 (the "License")
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace MongoDB.AspNetCore.OData;

internal class MongoExpressionRewriter : ExpressionVisitor
{
    private readonly MethodInfo _substringWithStart = typeof(string).GetMethod("Substring", new[] { typeof(int) });
    private readonly MethodInfo _substringWithLength =
        typeof(string).GetMethod("Substring", new[] { typeof(int), typeof(int) });

    protected override Expression VisitMethodCall(MethodCallExpression node) =>
        node.Method.Name switch
        {
            "SubstringStart" => Expression.Call(Visit(node.Arguments[0]), _substringWithStart,
                Visit(node.Arguments[1])),
            "SubstringStartAndLength" => Expression.Call(Visit(node.Arguments[0]), _substringWithLength,
                Visit(node.Arguments[1]), Visit(node.Arguments[2])),
            "Select" => VisitSelect(node),
            _ => base.VisitMethodCall(node)
        };

    private static Expression VisitSelect(MethodCallExpression node)
    {
        var source = node.Arguments[0];
        var lambda = (LambdaExpression)RemoveQuotes(node.Arguments[1]);

        var newLambda = (LambdaExpression) VisitSelectDynamic(lambda);

        return Expression.Call(
            typeof(Queryable),
            "Select",
            new[] { source.Type.GetGenericArguments()[0], newLambda.Type.GetGenericArguments()[1] },
            source,
            newLambda);
    }

    private static Expression RemoveQuotes(Expression e)
    {
        while (e.NodeType == ExpressionType.Quote)
        {
            e = ((UnaryExpression)e).Operand;
        }

        return e;
    }

    private static Expression VisitSelectSome(LambdaExpression lambda)
    {
        var body = lambda.Body;

        if (body is MemberInitExpression memberInit && memberInit.NewExpression.Type.Name.StartsWith("SelectSome"))
        {
            var containerBinding = memberInit.Bindings
                .OfType<MemberAssignment>()
                .FirstOrDefault(b => b.Member.Name == "Container");

            if (containerBinding != null)
            {
                var containerInit = (MemberInitExpression)containerBinding.Expression;
                var containerBindings = containerInit.Bindings.OfType<MemberAssignment>().ToList();

                var newBindings = GetNewBindings(containerBindings);

                return Expression.MemberInit(Expression.New(typeof(AnonymousType)), newBindings);
            }
        }

        return body;
    }

    private static MemberAssignment[] GetNewBindings(List<MemberAssignment> containerBindings)
    {
        int bindingCount = containerBindings.Count - 1;
        MemberAssignment[] newBindings = new MemberAssignment[bindingCount];

        for (int i = 0; i <= bindingCount; i++)
        {
            MemberAssignment currentBinding = containerBindings[i];

            if (currentBinding.Member.Name == "Name")
            {
                var valueBinding = containerBindings[i + 1];
                newBindings[i] = CreateNewBinding(currentBinding, valueBinding);
            }
            else if (currentBinding.Member.Name == "Value")
            {
            }
            else if (currentBinding.Member.Name.StartsWith("Next"))
            {
                if (currentBinding.Expression is MemberInitExpression nextInit &&
                    nextInit.Bindings[0] is MemberAssignment keyBinding &&
                    nextInit.Bindings[1] is MemberAssignment valueBinding)
                {
                    newBindings[i - 1] = CreateNewBinding(keyBinding, valueBinding);
                }
            }
            else
            {
                throw new NotSupportedException($"Unsupported binding type: {currentBinding.Member.Name}");
            }
        }

        return newBindings;
    }

    private static MemberAssignment CreateNewBinding(MemberAssignment nameBinding, MemberAssignment valueBinding)
    {
        string fieldName = (string)((ConstantExpression)nameBinding.Expression).Value;
        MemberInfo key = typeof(AnonymousType).GetProperty(fieldName);
        Expression value;

        if (valueBinding.Expression is ConditionalExpression ||
            valueBinding.Expression is BinaryExpression) // TODO: add more types if needed
        {
            value = valueBinding.Expression;
            return Expression.Bind(key, value);
        }

        throw new NotSupportedException($"Unsupported expression type: {valueBinding.Expression.Type}");
    }

    private class AnonymousType
    {
        public long? Id { get; set; }
        public string Name { get; set; }
        public double Area { get; set; }
    }


    private static Expression VisitSelectDynamic(LambdaExpression lambda)
    {
        var body = lambda.Body;

        if (body is MemberInitExpression memberInit && memberInit.NewExpression.Type.Name.StartsWith("SelectSome"))
        {
            var varExpando = Expression.Variable(typeof(ExpandoObject), "expando");
            var newExpando = Expression.New(typeof(ExpandoObject));

            var newBindings = new List<Expression> { Expression.Assign(varExpando, newExpando) };

            var containerBinding = memberInit.Bindings
                .OfType<MemberAssignment>()
                .FirstOrDefault(b => b.Member.Name == "Container");

            if (containerBinding != null)
            {
                var containerInit = (MemberInitExpression)containerBinding.Expression;
                var containerBindings = containerInit.Bindings.OfType<MemberAssignment>().ToList();

                for (int i = 0; i < containerBindings.Count; i++)
                {
                    MemberAssignment currentBinding = containerBindings[i];

                    if (currentBinding.Member.Name == "Name")
                    {
                        var valueBinding = containerBindings[i + 1];

                        var propertyName = (ConstantExpression)currentBinding.Expression;
                        var propertyValue = valueBinding.Expression;
                        var propertyValueAsObject = Expression.Convert(propertyValue, typeof(object));
                        var addMethod = typeof(IDictionary<string, object>).GetMethod("Add");
                        var addProperty = Expression.Call(
                            Expression.Convert(varExpando, typeof(IDictionary<string, object>)),
                            addMethod,
                            propertyName,
                            propertyValueAsObject);

                        newBindings.Add(addProperty);
                    }
                    else if (currentBinding.Member.Name == "Value")
                    {
                    }
                    else if (currentBinding.Member.Name.StartsWith("Next"))
                    {
                        if (currentBinding.Expression is MemberInitExpression nextInit &&
                            nextInit.Bindings[0] is MemberAssignment keyBinding &&
                            nextInit.Bindings[1] is MemberAssignment valueBinding)
                        {
                            var propertyName = (ConstantExpression)keyBinding.Expression;
                            var propertyValue = valueBinding.Expression;
                            var propertyValueAsObject = Expression.Convert(propertyValue, typeof(object));
                            var addMethod = typeof(IDictionary<string, object>).GetMethod("Add");
                            var addProperty = Expression.Call(
                                Expression.Convert(varExpando, typeof(IDictionary<string, object>)),
                                addMethod,
                                propertyName,
                                propertyValueAsObject);

                            newBindings.Add(addProperty);
                        }
                    }
                    else
                    {
                        throw new NotSupportedException($"Unsupported binding type: {currentBinding.Member.Name}");
                    }
                }

                newBindings.Add(varExpando);
                var block = Expression.Block(new[] { varExpando }, newBindings);

                var lambdaParameters = lambda.Parameters.ToArray();
                return Expression.Lambda(block, lambdaParameters);
            }
        }

        return body;
    }

}
