using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Nest;
using ProductCatalogService.Application.Common;

namespace ProductCatalogService.Application.SearchProducts
{
    public class GetCategoriesAuthzHandler : AuthorizationHandler<SearchProductsQuery>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SearchProductsQuery requirement)
        {
            if (context.User.HasClaim("user_role", "sys_admin") is true)
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }

    public class SearchProductsHandler : IRequestHandler<SearchProductsQuery, SearchProductsResponse>
    {
        private readonly IElasticClient _client;

        public SearchProductsHandler(IConfiguration config)
        {
            var connString = config.GetValue<string>("ElasticSearch:Url");
            var settings = new ConnectionSettings(new Uri(connString))
                .DefaultMappingFor<SearchProductModel>(i => i
                    .IndexName("product")
                )
                .PrettyJson();
            _client = new ElasticClient(settings);
        }

        public async Task<SearchProductsResponse> Handle(SearchProductsQuery request,
            CancellationToken cancellationToken)
        {
            Func<QueryContainerDescriptor<SearchProductModel>, QueryContainer> queryWithNameAndDesc = q => q
                .MultiMatch(mm => mm
                    .Query(request.Query)
                    .Fields(f => f
                        .Fields(f1 => f1.Name, f2 => f2.Description)));

            var result = await _client.SearchAsync<SearchProductModel>(s => s
                .Query(q =>
                    request.Query != "*"
                        ? queryWithNameAndDesc(q) && q.Range(ra => ra
                            .Field(f => f.Price)
                            .LessThanOrEquals(request.Price)
                        )
                        : q.Range(ra => ra
                            .Field(f => f.Price)
                            .LessThanOrEquals(request.Price))
                )
                .Aggregations(a => a
                        .Terms("category_tags", t => t
                            .Field(f => f.Category.Name.Suffix("keyword"))
                        ) && a
                        .Terms("inventory_tags", t => t
                            .Field(f => f.Inventory.Location.Suffix("keyword"))
                        )
                )
                .From(request.Page - 1)
                .Size(request.PageSize), cancellationToken);

            if (result.ApiCall.HttpStatusCode != (int)HttpStatusCode.OK)
            {
                throw new Exception("Could not query data");
            }

            var categoryTags = result
                .Aggregations
                .Terms("category_tags")
                .Buckets
                .Select(b => new SearchAggsByTagsDto(b.Key, (int) b.DocCount))
                .ToList();

            var inventoryTags = result
                .Aggregations
                .Terms("inventory_tags")
                .Buckets
                .Select(b => new SearchAggsByTagsDto(b.Key, (int) b.DocCount))
                .ToList();

            var items = result
                .Hits
                .Select(x => new SearchProductModel(x.Source.Id, x.Source.Name, x.Source.Price, x.Source.ImageUrl,
                    x.Source.Description,
                    new SearchCategoryModel(x.Source.Category != null ? x.Source.Category.Id : Guid.Empty,
                        x.Source.Category != null ? x.Source.Category.Name : string.Empty),
                    new SearchInventoryModel(
                        x.Source.Inventory != null ? x.Source.Inventory.Id : Guid.Empty,
                        x.Source.Inventory != null ? x.Source.Inventory.Location : string.Empty,
                        x.Source.Inventory != null ? x.Source.Inventory.Website : string.Empty,
                        x.Source.Inventory != null ? x.Source.Inventory.Description : string.Empty)))
                .ToList();

            var response = new SearchProductsResponse(
                (int)(result.Total / request.PageSize) + 1,
                result.Documents.Count,
                items.ToArray(),
                categoryTags.ToArray(),
                inventoryTags.ToArray(),
                (int)result.Took
            );

            return response;
        }
    }
}
