using System.Linq;
using System.Threading.Tasks;
using ManiDispatch.Application.DTOs;
using ManiDispatch.Application.Interfaces;
using ManiDispatch.Domain.Entities;

namespace ManiDispatch.Application.UseCases;

public class GetEligibleAlliesUseCase
{
    private readonly IAllyRepository _allyRepository;

    public GetEligibleAlliesUseCase(IAllyRepository allyRepository)
    {
        _allyRepository = allyRepository;
    }

    public async Task<MatchResponseDto> ExecuteAsync(MatchRequestDto request, string correlationId, string defaultTenantId = "default-tenant")
    {
        var tenantId = !string.IsNullOrWhiteSpace(request.TenantId) 
            ? request.TenantId 
            : defaultTenantId;

        var zonaId = !string.IsNullOrWhiteSpace(request.ZonaId) 
            ? request.ZonaId 
            : (!string.IsNullOrWhiteSpace(request.Location) ? request.Location : "zona-default");

        var categoriaId = !string.IsNullOrWhiteSpace(request.CategoriaId) 
            ? request.CategoriaId 
            : (!string.IsNullOrWhiteSpace(request.Category) ? request.Category : "cat-default");

        int page = request.Page.HasValue && request.Page.Value > 0 ? request.Page.Value : 1;
        int pageSize = request.PageSize.HasValue && request.PageSize.Value > 0 ? request.PageSize.Value : 20;

        var criteria = new MatchCriteria
        {
            TenantId = tenantId,
            ZonaId = zonaId,
            CategoriaId = categoriaId,
            Page = page,
            PageSize = pageSize
        };

        var (allies, totalCount) = await _allyRepository.GetEligibleAlliesAsync(criteria);

        var allyDtos = allies.Select(a => new AllyDto
        {
            AllyId = a.Id,
            Name = a.Nombre,
            Phone = a.Telefono,
            Rating = a.CalificacionPromedio,
            DistanceKm = a.DistanceKm ?? 1.5m,
            EstimatedArrivalMinutes = a.EstimatedArrivalMinutes ?? 20
        });

        return new MatchResponseDto
        {
            Message = "Aliados válidos obtenidos con éxito por cobertura y categoría (RF-12).",
            CorrelationId = correlationId,
            TenantId = tenantId,
            ZonaId = zonaId,
            CategoriaId = categoriaId,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            MatchedAllies = allyDtos
        };
    }
}
