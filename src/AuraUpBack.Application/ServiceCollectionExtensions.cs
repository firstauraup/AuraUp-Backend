using AuraUpBack.Application.Abstractions;
using AuraUpBack.Application.Commands.CreateExplorationRequest;
using AuraUpBack.Application.Commands.ConnectInstagramIntegration;
using AuraUpBack.Application.Commands.CompleteInstagramManualLogin;
using AuraUpBack.Application.Commands.BackfillTrackedAccountHistory;
using AuraUpBack.Application.Commands.DeleteTrackedAccount;
using AuraUpBack.Application.Commands.GenerateViralReelIdeas;
using AuraUpBack.Application.Commands.InspectTrackedAccount;
using AuraUpBack.Application.Commands.RegisterTrackedAccount;
using AuraUpBack.Application.Commands.ReconnectInstagramIntegration;
using AuraUpBack.Application.Commands.RunExplorationRequest;
using AuraUpBack.Application.Commands.StartInstagramManualLogin;
using AuraUpBack.Application.Commands.TranscribeTrackedPost;
using AuraUpBack.Application.Commands.UpdateTrackedAccountMonitoring;
using AuraUpBack.Application.Commands.VerifyInstagramIntegrationCode;
using AuraUpBack.Application.Queries.GetInstagramIntegrationStatus;
using AuraUpBack.Application.Queries.GetInstagramExplorerAccountPreview;
using AuraUpBack.Application.Queries.GetTrackedAccountAnalysis;
using AuraUpBack.Application.Queries.GetTrackedAccountOverview;
using AuraUpBack.Application.Queries.GetWatchlistDashboard;
using AuraUpBack.Application.Queries.SearchInstagramExplorer;
using Microsoft.Extensions.DependencyInjection;

namespace AuraUpBack.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<ICommandDispatcher, CommandDispatcher>();
        services.AddSingleton<IQueryDispatcher, QueryDispatcher>();

        services.AddSingleton<ICommandHandler<ConnectInstagramIntegrationCommand, Contracts.InstagramIntegrationDto>, ConnectInstagramIntegrationCommandHandler>();
        services.AddSingleton<ICommandHandler<CompleteInstagramManualLoginCommand, Contracts.InstagramIntegrationDto>, CompleteInstagramManualLoginCommandHandler>();
        services.AddSingleton<ICommandHandler<BackfillTrackedAccountHistoryCommand, Contracts.BackfillTrackedAccountHistoryDto>, BackfillTrackedAccountHistoryCommandHandler>();
        services.AddSingleton<ICommandHandler<DeleteTrackedAccountCommand, bool>, DeleteTrackedAccountCommandHandler>();
        services.AddSingleton<ICommandHandler<GenerateViralReelIdeasCommand, Contracts.ViralIdeaGenerationResultDto>, GenerateViralReelIdeasCommandHandler>();
        services.AddSingleton<ICommandHandler<RegisterTrackedAccountCommand, Contracts.TrackedAccountOverviewDto>, RegisterTrackedAccountCommandHandler>();
        services.AddSingleton<ICommandHandler<InspectTrackedAccountCommand, Contracts.TrackedAccountOverviewDto>, InspectTrackedAccountCommandHandler>();
        services.AddSingleton<ICommandHandler<ReconnectInstagramIntegrationCommand, Contracts.InstagramIntegrationDto>, ReconnectInstagramIntegrationCommandHandler>();
        services.AddSingleton<ICommandHandler<StartInstagramManualLoginCommand, Contracts.InstagramIntegrationDto>, StartInstagramManualLoginCommandHandler>();
        services.AddSingleton<ICommandHandler<VerifyInstagramIntegrationCodeCommand, Contracts.InstagramIntegrationDto>, VerifyInstagramIntegrationCodeCommandHandler>();
        services.AddSingleton<ICommandHandler<CreateExplorationRequestCommand, Contracts.ExplorationRequestDto>, CreateExplorationRequestCommandHandler>();
        services.AddSingleton<ICommandHandler<RunExplorationRequestCommand, Contracts.ExplorationRequestDto>, RunExplorationRequestCommandHandler>();
        services.AddSingleton<ICommandHandler<TranscribeTrackedPostCommand, Contracts.TranscriptionResultDto>, TranscribeTrackedPostCommandHandler>();
        services.AddSingleton<ICommandHandler<UpdateTrackedAccountMonitoringCommand, Contracts.TrackedAccountOverviewDto>, UpdateTrackedAccountMonitoringCommandHandler>();
        services.AddSingleton<IQueryHandler<GetInstagramIntegrationStatusQuery, Contracts.InstagramIntegrationDto>, GetInstagramIntegrationStatusQueryHandler>();
        services.AddSingleton<IQueryHandler<GetInstagramExplorerAccountPreviewQuery, Contracts.ExplorerAccountPreviewDto>, GetInstagramExplorerAccountPreviewQueryHandler>();
        services.AddSingleton<IQueryHandler<GetTrackedAccountAnalysisQuery, Contracts.TrackedAccountAnalysisDto>, GetTrackedAccountAnalysisQueryHandler>();
        services.AddSingleton<IQueryHandler<GetTrackedAccountOverviewQuery, Contracts.TrackedAccountOverviewDto>, GetTrackedAccountOverviewQueryHandler>();
        services.AddSingleton<IQueryHandler<GetWatchlistDashboardQuery, Contracts.WatchlistDashboardDto>, GetWatchlistDashboardQueryHandler>();
        services.AddSingleton<IQueryHandler<SearchInstagramExplorerQuery, Contracts.ExplorerSearchResultDto>, SearchInstagramExplorerQueryHandler>();

        return services;
    }
}
