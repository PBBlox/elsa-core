using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Elsa.ActivityResults;
using Elsa.Builders;
using Elsa.Events;
using Elsa.Exceptions;
using Elsa.Models;
using Elsa.Services.Models;
using Elsa.Triggers;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Elsa.Services
{
    public class WorkflowHost : IWorkflowHost
    {
        private delegate ValueTask<IActivityExecutionResult> ActivityOperation(ActivityExecutionContext activityExecutionContext, IActivity activity, CancellationToken cancellationToken);

        private static readonly ActivityOperation Execute = (context, activity, cancellationToken) => activity.ExecuteAsync(context, cancellationToken);
        private static readonly ActivityOperation Resume = (context, activity, cancellationToken) => activity.ResumeAsync(context, cancellationToken);

        private readonly IWorkflowRegistry _workflowRegistry;
        private readonly IWorkflowFactory _workflowFactory;
        private readonly IWorkflowBuilder _workflowBuilder;
        private readonly IMediator _mediator;
        private readonly IServiceProvider _serviceProvider;
        private readonly IEnumerable<ITriggerProvider> _triggerProviders;
        private readonly ILogger _logger;

        public WorkflowHost(
            IWorkflowRegistry workflowRegistry,
            IWorkflowFactory workflowFactory,
            IWorkflowBuilder workflowBuilder,
            IMediator mediator,
            IServiceProvider serviceProvider,
            IEnumerable<ITriggerProvider> triggerProviders,
            ILogger<WorkflowHost> logger)
        {
            _workflowRegistry = workflowRegistry;
            _workflowFactory = workflowFactory;
            _workflowBuilder = workflowBuilder;
            _mediator = mediator;
            _serviceProvider = serviceProvider;
            _triggerProviders = triggerProviders;
            _logger = logger;
        }

        public async ValueTask<WorkflowInstance> RunWorkflowAsync(
            IWorkflowBlueprint workflowBlueprint,
            string? activityId = default,
            object? input = default,
            string? correlationId = default,
            CancellationToken cancellationToken = default)
        {
            var workflowInstance = await _workflowFactory.InstantiateAsync(
                workflowBlueprint,
                correlationId,
                cancellationToken);

            return await RunWorkflowAsync(workflowBlueprint, workflowInstance, activityId, input, cancellationToken);
        }

        public async ValueTask<WorkflowInstance> RunWorkflowAsync<T>(
            string? activityId = default,
            object? input = default,
            string? correlationId = default,
            CancellationToken cancellationToken = default)
            where T : IWorkflow =>
            await RunWorkflowAsync(_workflowBuilder.Build<T>(), activityId, input, correlationId, cancellationToken);

        public async ValueTask<WorkflowInstance> RunWorkflowAsync<T>(
            WorkflowInstance workflowInstance,
            string? activityId = default,
            object? input = default,
            CancellationToken cancellationToken = default)
            where T : IWorkflow =>
            await RunWorkflowAsync(_workflowBuilder.Build<T>(), workflowInstance, activityId, input, cancellationToken);

        public async ValueTask<WorkflowInstance> RunWorkflowAsync(
            WorkflowInstance workflowInstance,
            string? activityId = default,
            object? input = default,
            CancellationToken cancellationToken = default)
        {
            var workflowBlueprint = await _workflowRegistry.GetWorkflowAsync(
                workflowInstance.WorkflowDefinitionId,
                VersionOptions.SpecificVersion(workflowInstance.Version),
                cancellationToken);

            if (workflowBlueprint == null)
                throw new WorkflowException($"Workflow instance {workflowInstance.Id} references workflow definition {workflowInstance.WorkflowDefinitionId} version {workflowInstance.Version}, but no such workflow definition was found.");

            return await RunWorkflowAsync(workflowBlueprint, workflowInstance, activityId, input, cancellationToken);
        }

        public async ValueTask<WorkflowInstance> RunWorkflowAsync(
            IWorkflowBlueprint workflowBlueprint,
            WorkflowInstance workflowInstance,
            string? activityId = default,
            object? input = default,
            CancellationToken cancellationToken = default)
        {
            var workflowExecutionContext = CreateWorkflowExecutionContext(workflowBlueprint, workflowInstance, _serviceProvider);
            var activity = activityId != null ? workflowBlueprint.GetActivity(activityId) : default;

            switch (workflowExecutionContext.Status)
            {
                case WorkflowStatus.Idle:
                    await BeginWorkflow(workflowExecutionContext, activity, input, cancellationToken);
                    break;

                case WorkflowStatus.Running:
                    await RunWorkflowAsync(workflowExecutionContext, cancellationToken);
                    break;

                case WorkflowStatus.Suspended:
                    await ResumeWorkflowAsync(workflowExecutionContext, activity, input, cancellationToken);
                    break;
            }

            await _mediator.Publish(new WorkflowExecuted(workflowExecutionContext), cancellationToken);

            var statusEvent = default(object);

            switch (workflowExecutionContext.Status)
            {
                case WorkflowStatus.Cancelled:
                    statusEvent = new WorkflowCancelled(workflowExecutionContext);
                    break;

                case WorkflowStatus.Completed:
                    statusEvent = new WorkflowCompleted(workflowExecutionContext);
                    break;

                case WorkflowStatus.Faulted:
                    statusEvent = new WorkflowFaulted(workflowExecutionContext);
                    break;

                case WorkflowStatus.Suspended:
                    statusEvent = new WorkflowSuspended(workflowExecutionContext);
                    break;
            }

            if (statusEvent != null)
                await _mediator.Publish(statusEvent, cancellationToken);

            return workflowExecutionContext.WorkflowInstance;
        }

        private async Task BeginWorkflow(
            WorkflowExecutionContext workflowExecutionContext,
            IActivityBlueprint? activity,
            object? input,
            CancellationToken cancellationToken)
        {
            if (activity == null)
                activity = workflowExecutionContext.WorkflowBlueprint.GetStartActivities().First();

            if (!await CanExecuteAsync(workflowExecutionContext, activity, input, cancellationToken))
                return;

            workflowExecutionContext.Begin();
            workflowExecutionContext.ScheduleActivity(activity.Id, input);
            await RunAsync(workflowExecutionContext, Execute, cancellationToken);
        }

        private async Task RunWorkflowAsync(
            WorkflowExecutionContext workflowExecutionContext,
            CancellationToken cancellationToken)
        {
            await RunAsync(workflowExecutionContext, Execute, cancellationToken);
        }

        private async Task ResumeWorkflowAsync(
            WorkflowExecutionContext workflowExecutionContext,
            IActivityBlueprint activityBlueprint,
            object? input,
            CancellationToken cancellationToken)
        {
            if (!await CanExecuteAsync(workflowExecutionContext, activityBlueprint, input, cancellationToken))
                return;

            workflowExecutionContext.WorkflowInstance.BlockingActivities.RemoveWhere(x => x.ActivityId == activityBlueprint.Id);
            workflowExecutionContext.Resume();
            workflowExecutionContext.ScheduleActivity(activityBlueprint.Id, input);
            await RunAsync(workflowExecutionContext, Resume, cancellationToken);
        }

        private async ValueTask<bool> CanExecuteAsync(
            WorkflowExecutionContext workflowExecutionContext,
            IActivityBlueprint activityBlueprint,
            object? input,
            CancellationToken cancellationToken)
        {
            var activityExecutionContext = new ActivityExecutionContext(
                workflowExecutionContext,
                _serviceProvider,
                activityBlueprint,
                input);

            var activity = await activityBlueprint.CreateActivityAsync(activityExecutionContext, cancellationToken);
            return await activity.CanExecuteAsync(activityExecutionContext, cancellationToken);
        }

        private async ValueTask RunAsync(
            WorkflowExecutionContext workflowExecutionContext,
            ActivityOperation activityOperation,
            CancellationToken cancellationToken = default)
        {
            while (workflowExecutionContext.HasScheduledActivities)
            {
                var scheduledActivity = workflowExecutionContext.PopScheduledActivity();
                var currentActivityId = scheduledActivity.ActivityId;
                var activityBlueprint = workflowExecutionContext.WorkflowBlueprint.GetActivity(currentActivityId)!;
                var activityExecutionContext = new ActivityExecutionContext(workflowExecutionContext, _serviceProvider, activityBlueprint, scheduledActivity.Input);
                var activity = await activityBlueprint.CreateActivityAsync(activityExecutionContext, cancellationToken);
                var result = await activityOperation(activityExecutionContext, activity, cancellationToken);
                await _mediator.Publish(new ActivityExecuting(activityExecutionContext), cancellationToken);
                await result.ExecuteAsync(activityExecutionContext, cancellationToken);
                await _mediator.Publish(new ActivityExecuted(activityExecutionContext), cancellationToken);

                activityOperation = Execute;
                workflowExecutionContext.CompletePass();
            }

            if (workflowExecutionContext.Status == WorkflowStatus.Running)
                workflowExecutionContext.Complete();
        }

        private static WorkflowExecutionContext CreateWorkflowExecutionContext(IWorkflowBlueprint workflowBlueprint, WorkflowInstance workflowInstance, IServiceProvider serviceProvider) =>
            new WorkflowExecutionContext(serviceProvider, workflowBlueprint, workflowInstance);
    }
}