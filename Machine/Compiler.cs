using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Sawmill;
using I = Amateurlog.Machine.Instruction;

namespace Amateurlog.Machine
{
    static class Compiler
    {
        public static Program Compile(ImmutableArray<Rule> program)
        {
            var atoms = program
                .Select(GetAtoms)
                .Aggregate(Enumerable.Union)
                .ToImmutableArray();
            var atomsLookup = atoms
                .Select((x, i) => new KeyValuePair<string, int>(x, i))
                .ToImmutableDictionary();

            var code = new List<Instruction>();

            code.Add(new I.Call(atomsLookup["main"] << 8, 0));
            code.Add(new I.End());

            foreach (var group in program.GroupBy(r => r.Head.Name))
            {
                code.AddRange(CompileProcedure(group, atomsLookup));
            }

            return new Program(atoms, code.ToImmutableArray());
        }

        private static IEnumerable<Instruction> CompileProcedure(IEnumerable<Rule> clauses, ImmutableDictionary<string, int> atoms)
            => clauses.SelectMany((rule, i) => CompileClause(rule, i, clauses.Count(), atoms));

        private static IEnumerable<Instruction> CompileClause(
            Rule rule,
            int clauseNumber,
            int clauseCount,
            ImmutableDictionary<string, int> atoms
        )
        {
            if (clauseCount > 256)
            {
                throw new Exception();
            }

            var labelId = (atoms[rule.Head.Name] << 8) | clauseNumber;
            yield return new I.Label(labelId);

            if (clauseCount > 1)
            {
                if (clauseNumber == 0)
                {
                    yield return new I.Try(labelId + 1);
                }
                else if (clauseNumber == clauseCount - 1)
                {
                    yield return new I.CatchAll();
                }
                else
                {
                    yield return new I.Catch(labelId + 1);
                }
            }

            var (variables, preamble) = AllocateVariables(rule.Body.Concat(new[]{rule.Head}));

            foreach (var i in preamble)
            {
                yield return i;
            }

            foreach (var (arg, argNum) in rule.Head.Args.Select((x, i) => (x, i)))
            {
                yield return new I.LoadArg(argNum);
                foreach (var i in MatchTerm(arg, atoms, variables))
                {
                    yield return i;
                }
            }

            foreach (var i in rule.Body.SelectMany(goal => CallPredicate(goal, atoms, variables)))
            {
                yield return i;
            }

            yield return new I.Return();
        }

        private static (ImmutableDictionary<string, int>, IEnumerable<Instruction>) AllocateVariables(IEnumerable<Term> terms)
        {
            var variables = terms
                .SelectMany(t => t.Variables())
                .Distinct()
                .OrderBy(x => x)
                .Select((x, i) => new KeyValuePair<string, int>(x, i + 1))
                .ToImmutableDictionary();
            
            IEnumerable<Instruction> Code()
            {
                yield return new I.Allocate(variables.Count + 1);

                for (var i = 1; i < variables.Count + 1; i++)
                {
                    yield return new I.CreateVariable();
                    yield return new I.StoreLocal(i);
                }
            }
            return (variables, Code());
        }

        // With (the address of) a heap object on the stack,
        // match the heap object to the term, binding any variables,
        // and pop the address from the stack
        private static IEnumerable<Instruction> MatchTerm(
            Term term,
            ImmutableDictionary<string, int> atoms,
            ImmutableDictionary<string, int> variables
        ) => term
            .SelfAndDescendants()
            .SelectMany(x =>
                x switch
                {
                    Predicate p =>
                        new Instruction[]
                        {
                            new I.Dup(),
                            new I.GetObject(atoms[p.Name], p.Args.Length),
                            new I.StoreLocal(0),
                            new I.LoadLocal(0),
                            new I.Unify()
                        }.Concat(
                            Enumerable
                                .Range(0, p.Args.Length)
                                .Reverse()
                                .SelectMany(i => new Instruction[] { new I.LoadLocal(0), new I.LoadField(i) })
                        ),
                    Atom a => new Instruction[] { new I.Dup(), new I.GetObject(atoms[a.Value], 0), new I.Unify() },
                    Variable v => new Instruction[] { new I.LoadLocal(variables[v.Name]), new I.Unify() },
                    _ => throw new Exception()
                }
            );

        private static IEnumerable<Instruction> CallPredicate(
            Predicate goal,
            ImmutableDictionary<string, int> atoms,
            ImmutableDictionary<string, int> variables
        )
        {
            if (goal.Name == "dump")
            {
                var variable = (Variable)goal.Args[0];
                yield return new I.Write(variable.Name + " := ");
                yield return new I.LoadLocal(variables[variable.Name]);
                yield return new I.Dump();
                yield return new I.Write(Environment.NewLine);
                yield break;
            }

            foreach (var arg in goal.Args.Reverse())
            {
                yield return new I.CreateVariable();
                yield return new I.Dup();
                foreach (var i in BuildTerm(arg, atoms, variables))
                {
                    yield return i;
                }
            }

            yield return new I.Call(atoms[goal.Name] << 8, goal.Args.Length);
        }

        // With (the address of) an unbound variable on the stack,
        // build the term, bind the variable to the term, and pop the
        // address from the stack
        private static IEnumerable<Instruction> BuildTerm(
            Term term,
            ImmutableDictionary<string, int> atoms,
            ImmutableDictionary<string, int> variables
        ) => term
            .SelfAndDescendants()
            .SelectMany(x =>
                x switch
                {
                    Predicate p =>
                        new Instruction[]
                        {
                            new I.CreateObject(atoms[p.Name], p.Args.Length),
                            new I.StoreLocal(0),
                            new I.LoadLocal(0),
                            new I.Bind()
                        }.Concat(
                            Enumerable.Range(0, p.Args.Length)
                                .Reverse()
                                .SelectMany(i => new Instruction[] { new I.LoadLocal(0), new I.LoadField(i) })
                        ),
                    Atom a => new Instruction[] { new I.CreateObject(atoms[a.Value], 0), new I.Bind() },
                    Variable v => new Instruction[] { new I.LoadLocal(variables[v.Name]), new I.Bind() },
                    _ => throw new Exception()
                }
            );

        private static IEnumerable<string> GetAtoms(Rule rule)
            => rule.Body
                .Select(GetAtoms)
                .Aggregate(Enumerable.Empty<string>(), Enumerable.Union)
                .Union(GetAtoms(rule.Head));

        private static IEnumerable<string> GetAtoms(Term term)
            => term
                .SelfAndDescendants()
                .Select(x => x switch
                {
                    Predicate p => p.Name,
                    Atom a => a.Value,
                    _ => null
                })
                .Where(name => name != null)
                .Distinct()
                .OrderBy(x => x);
    }
}
