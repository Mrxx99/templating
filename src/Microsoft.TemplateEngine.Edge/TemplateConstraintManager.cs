﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.TemplateEngine.Abstractions;

namespace Microsoft.TemplateEngine.Edge
{
    /// <summary>
    /// Manages evaluation of constraints for the templates.
    /// </summary>
    public class TemplateConstraintManager : IDisposable
    {
        private readonly IEngineEnvironmentSettings _engineEnvironmentSettings;
        private readonly ILogger<TemplateConstraintManager> _logger;
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private Dictionary<string, Task<ITemplateConstraint>> _templateConstrains = new Dictionary<string, Task<ITemplateConstraint>>();

        public TemplateConstraintManager(IEngineEnvironmentSettings engineEnvironmentSettings)
        {
            _engineEnvironmentSettings = engineEnvironmentSettings;
            _logger = _engineEnvironmentSettings.Host.LoggerFactory.CreateLogger<TemplateConstraintManager>();

            var constraintFactories = engineEnvironmentSettings.Components.OfType<ITemplateConstraintFactory>();
            _logger.LogDebug($"Found {constraintFactories.Count()} constraints factories, initializing.");
            foreach (var constraintFactory in constraintFactories)
            {
                _templateConstrains[constraintFactory.Type] = Task.Run (() => constraintFactory.CreateTemplateConstraintAsync(engineEnvironmentSettings, _cancellationTokenSource.Token));
            }

        }

        /// <summary>
        /// Returns the list of initialized <see cref="ITemplateConstraint"/>s.
        /// Only returns the list of <see cref="ITemplateConstraint"/> that were initialized successfully.
        /// The constraints which failed to be initialized are skipped and warning is logged.
        /// </summary>
        /// <param name="templates">if given, only returns the list of constraints defined in the templates.</param>
        /// <param name="cancellationToken"></param>
        /// <returns>The list of successfully initialized <see cref="ITemplateConstraint"/>s.</returns>
        public async Task<IReadOnlyList<ITemplateConstraint>> GetConstraintsAsync(IReadOnlyList<ITemplateInfo>? templates = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<(string Type, Task<ITemplateConstraint> Task)> constraintsToInitialize;
            if (templates?.Any() ?? false)
            {
                List<string> uniqueConstraints = templates.SelectMany(ti => ti.Constraints.Select(c => c.Type)).Distinct().ToList();
                constraintsToInitialize = _templateConstrains.Where(kvp => uniqueConstraints.Contains(kvp.Key)).Select(kvp => (kvp.Key, kvp.Value));
            }
            else
            {
                constraintsToInitialize = _templateConstrains.Select(kvp => (kvp.Key, kvp.Value));
            }

            try
            {
                _logger.LogDebug($"Waiting for {constraintsToInitialize.Count()} to be initialized initialized.");
                await CancellableWhenAll(constraintsToInitialize.Select(c => c.Task), cancellationToken).ConfigureAwait(false);
                _logger.LogDebug($"{constraintsToInitialize.Count()} constraints were initialized.");
                return constraintsToInitialize.Select(c => c.Task.Result).ToList();
            }
            catch (TaskCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                foreach (var constraint in constraintsToInitialize)
                {
                    if (constraint.Task.IsFaulted || constraint.Task.IsCanceled)
                    {
                        //TODO: localize
                        _logger.LogWarning($"The constraint of type {constraint.Type} failed to initialize: {constraint.Task.Exception.Message}.");
                        _logger.LogDebug($"Details: {constraint.Task.Exception}.");
                    }
                }
                _logger.LogDebug($"{constraintsToInitialize.Count(c => c.Task.Status == TaskStatus.RanToCompletion)} constraints were initialized.");
                return constraintsToInitialize
                    .Where(c => c.Task.Status == TaskStatus.RanToCompletion)
                    .Select(c => c.Task.Result)
                    .ToList();
            }

        }

        /// <summary>
        /// Evaluates the constraints with given <paramref name="type"/> for given args <paramref name="args"/>.
        /// </summary>
        /// <param name="type">constraint type to evaluate.</param>
        /// <param name="args">arguments to use for evaluation.</param>
        /// <param name="cancellationToken"></param>
        /// <returns><see cref="TemplateConstraintResult"/> indicating if constraint is met, or details why the constraint is not met.</returns>
        /// <exception cref="UnknownConstraintException">when the constraint of type <paramref name="type"/> is unknown.</exception>
        /// <exception cref="ConstraintInitializationException">when the constraint of type <paramref name="type"/> failed to initialize.</exception>
        /// <exception cref="ConstraintEvaluationException">when the constraint of type <paramref name="type"/> failed to evaluate for <paramref name="args"/>.</exception>
        public async Task<TemplateConstraintResult> EvaluateConstraintAsync(string type, string? args, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_templateConstrains.TryGetValue(type, out Task<ITemplateConstraint> task))
            {
                _logger.LogDebug($"The constraint '{type}' is unknown.");
                throw new UnknownConstraintException(type);
            }

            if (!task.IsCompleted)
            {
                try
                {
                    _logger.LogDebug($"The constraint '{type}' is not initialized, waiting for initialization.");
                    await CancellableWhenAll(new[] { task }, cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug($"The constraint '{type}' is initialized successfully.");
                }
                catch (TaskCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    //handled below
                }
            }
            if (task.IsFaulted || task.IsCanceled)
            {
                _logger.LogDebug($"The constraint '{type}' failed to be initialized, details: {task.Exception}.");
                throw new ConstraintInitializationException(type, task.Exception);
            }

            try
            {
                return task.Result.Evaluate(args);
            }
            catch (Exception e)
            {
                _logger.LogDebug($"The constraint '{type}' failed to be evaluated for the args '{args}', details: {e}.");
                throw new ConstraintEvaluationException(task.Result, e);
            }

        }

        /// <summary>
        /// Evaluates the constraints with given <paramref name="templates"/>.
        /// The method doesn't throw when the constraint is failed to be evaluated, returns <see cref="TemplateConstraintResult"/> with status <see cref="TemplateConstraintResult.Status.NotEvaluated"/> instead.
        /// </summary>
        /// <param name="templates">the list of templates to evaluate constraints for given templates.</param>
        /// <param name="cancellationToken"></param>
        /// <returns><see cref="TemplateConstraintResult"/> indicating if constraint is met, or details why the constraint is not met.</returns>
        public async Task<IReadOnlyList<(ITemplateInfo Template, IReadOnlyList<TemplateConstraintResult> Result)>> EvaluateConstraintsAsync(IReadOnlyList<ITemplateInfo> templates, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var requiredConstraints = templates.SelectMany(t => t.Constraints).Select(c => c.Type).Distinct();
            var tasksToWait = new List<Task>();
            foreach (var constraintType in requiredConstraints)
            {
                if (!_templateConstrains.TryGetValue(constraintType, out Task<ITemplateConstraint> task))
                {
                    //handled below
                    continue;
                }
                tasksToWait.Add(task);
            }

            if (tasksToWait.Any(t => !t.IsCompleted))
            {
                try
                {
                    var notCompletedTasks = tasksToWait.Where(t => !t.IsCompleted);
                    _logger.LogDebug($"The constraint(s) are not initialized, waiting for initialization.");
                    await CancellableWhenAll(notCompletedTasks, cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug($"The constraint(s) are initialized successfully.");
                }
                catch (TaskCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    //handled below
                }
            }
            cancellationToken.ThrowIfCancellationRequested();

            List<(ITemplateInfo, IReadOnlyList<TemplateConstraintResult>)> evaluationResult = new();
            foreach (ITemplateInfo template in templates)
            {
                List<TemplateConstraintResult> constraintResults = new();
                foreach (var constraint in template.Constraints)
                {
                    if (!_templateConstrains.TryGetValue(constraint.Type, out Task<ITemplateConstraint> task))
                    {
                        _logger.LogDebug($"The constraint '{constraint.Type}' is unknown.");
                        constraintResults.Add(TemplateConstraintResult.CreateFailure(constraint, $"The constraint '{constraint.Type}' is unknown."));
                        continue;
                    }

                    if (task.IsFaulted || task.IsCanceled)
                    {
                        _logger.LogDebug($"The constraint '{constraint.Type}' failed to be initialized, details: {task.Exception}.");
                        constraintResults.Add(TemplateConstraintResult.CreateFailure(constraint, $"The constraint '{constraint.Type}' failed to be initialized, details: {task.Exception.Message}."));
                        continue;
                    }

                    try
                    {
                        constraintResults.Add(task.Result.Evaluate(constraint.Args));
                    }
                    catch (Exception e)
                    {
                        _logger.LogDebug($"The constraint '{constraint.Type}' failed to be evaluated for the args '{constraint.Args}', details: {e}.");
                        constraintResults.Add(TemplateConstraintResult.CreateFailure(constraint, $"The constraint '{constraint.Type}' failed to be evaluated for the args '{constraint.Args}', details: {e.Message}."));
                    }
                }
                evaluationResult.Add((template, constraintResults));
            }
            return evaluationResult;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
        }

        private async Task CancellableWhenAll(IEnumerable<Task> tasks, CancellationToken cancellationToken)
        {
            await Task.WhenAny(
                Task.WhenAll(tasks),
                Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                throw new TaskCanceledException();
            }

            //throws exceptions
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        #region Exceptions

        public class UnknownConstraintException : Exception
        {
            public UnknownConstraintException(string type)
                : base($"The constraint of type '{type}' is unknown.")
            {
                Type = type;
            }

            public string Type { get; }
        }

        public class ConstraintEvaluationException : Exception
        {
            public ConstraintEvaluationException(ITemplateConstraint constraint, Exception? innerException = null)
                : base($"The constraint '{constraint.DisplayName}' failed to evaluate.", innerException)
            {
                Constraint = constraint;
            }

            public ITemplateConstraint Constraint { get; }
        }

        public class ConstraintInitializationException : Exception
        {
            public ConstraintInitializationException(string type, Exception? innerException = null)
                : base($"The constraint of type '{type}' failed to initialize.", innerException)
            {
                Type = type;
            }

            public string Type { get; }
        }

        #endregion
    }
}
