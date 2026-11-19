using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Domain.Entities;
using Moq;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>
/// SCRUM-1652: la lectura de Core (contexto, elegibles y criterio) y la de rechazos, que es de Dispatch (ADR-0030), van a
/// la vez. Cada doble espera a que la otra lectura haya empezado; si el caso de uso volviera a pedirlas una tras otra,
/// la espera vence y la prueba falla en vez de colgarse.
/// </summary>
public class GetEligibleAlliesConcurrencyTests
{
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(5);

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _requestId = Guid.NewGuid();
    private readonly Guid _usuarioId = Guid.NewGuid();
    private readonly Mock<IAllyRepository> _repo = new();
    private readonly Mock<ISolicitudAssignmentRepository> _assignments = new();
    private readonly Mock<IRulesClient> _rules = new();

    private GetEligibleAlliesUseCase NewUseCase() => new(_repo.Object, _assignments.Object, _rules.Object);

    private EligibleForRequest Lectura(params Ally[] allies) => new()
    {
        Solicitud = new SolicitudContext { Id = _requestId, ZonaId = Guid.NewGuid(), CategoriaId = Guid.NewGuid(), Estado = "PENDIENTE" },
        CriterioOrdenListado = "CALIFICACION",
        Allies = allies.ToList(),
        TotalCount = allies.Length
    };

    private Task<Application.DTOs.MatchResponseDto> Execute() =>
        NewUseCase().ExecuteForRequestAsync(_requestId, _tenantId, _usuarioId, "cliente", null, null, null, "corr");

    [Fact]
    public async Task CoreAndRejections_AreReadAtTheSameTime_AndRejectedAlliesAreLeftOut()
    {
        var corePedido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rechazosPedidos = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queda = new Ally { Id = Guid.NewGuid(), NombreRazonSocial = "Queda" };
        var rechazo = new Ally { Id = Guid.NewGuid(), NombreRazonSocial = "Ya rechazó" };

        _repo.Setup(r => r.GetEligibleForRequestAsync(_requestId, _tenantId, null, 1, 100, "corr")).Returns(async () =>
        {
            corePedido.TrySetResult();
            await rechazosPedidos.Task.WaitAsync(Espera);
            return Lectura(queda, rechazo);
        });
        _assignments.Setup(a => a.GetRejectedAllyIdsAsync(_requestId, _tenantId, _usuarioId, "cliente")).Returns(async () =>
        {
            rechazosPedidos.TrySetResult();
            await corePedido.Task.WaitAsync(Espera);
            return (IReadOnlyList<Guid>)new[] { rechazo.Id };
        });
        _rules.Setup(r => r.RankAlliesAsync("CALIFICACION", It.IsAny<IReadOnlyCollection<Ally>>(), _tenantId, "cliente", "corr"))
              .ReturnsAsync((string _, IReadOnlyCollection<Ally> c, Guid _, string _, string _) =>
                  c.Select((a, i) => new RankedAlly(a.Id, i + 1, null, Array.Empty<string>())).ToList());

        var result = await Execute();

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(queda.Id.ToString(), result.MatchedAllies.Single().AllyId);
        _rules.Verify(r => r.RankAlliesAsync("CALIFICACION", It.Is<IReadOnlyCollection<Ally>>(c => c.Single().Id == queda.Id),
            _tenantId, "cliente", "corr"), Times.Once);
    }

    [Fact]
    public async Task WhenTheRequestIsNotVisible_ItIsStillA404_EvenIfRejectionsFail()
    {
        _repo.Setup(r => r.GetEligibleForRequestAsync(_requestId, _tenantId, It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), "corr"))
             .ReturnsAsync((EligibleForRequest?)null);
        _assignments.Setup(a => a.GetRejectedAllyIdsAsync(_requestId, _tenantId, _usuarioId, "cliente"))
                    .ThrowsAsync(new InvalidOperationException("base caída"));

        await Assert.ThrowsAsync<KeyNotFoundException>(Execute);
    }

    [Fact]
    public async Task WhenAllEligiblesAlreadyRejected_ReturnsAnEmptyListWithoutCallingRules()
    {
        var unico = new Ally { Id = Guid.NewGuid() };
        _repo.Setup(r => r.GetEligibleForRequestAsync(_requestId, _tenantId, It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), "corr"))
             .ReturnsAsync(Lectura(unico));
        _assignments.Setup(a => a.GetRejectedAllyIdsAsync(_requestId, _tenantId, _usuarioId, "cliente"))
                    .ReturnsAsync(new[] { unico.Id });

        var result = await Execute();

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.MatchedAllies);
        _rules.VerifyNoOtherCalls();
    }
}
