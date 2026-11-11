using GarageV3.Data;
using GarageV3.Models.Entities;
using GarageV3.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace GarageV3.Services
{
    public class ParkingAllocationService : IParkingAllocationService
    {
        private readonly ApplicationDbContext _context;

        public ParkingAllocationService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<AllocationResult> AllocateAndStartSessionAsync(int vehicleId, int? preferredSpotId, decimal hourlyRate)
        {
            var vehicle = await _context.Vehicles
                .Include(v => v.VehicleTypeRef)
                .FirstOrDefaultAsync(v => v.Id == vehicleId);

            if (vehicle?.VehicleTypeRef == null)
            {
                return AllocationResult.Fail("Vehicle or vehicle type not found.");
            }

            var vehicleType = vehicle.VehicleTypeRef;
            var plan = await BuildAllocationPlanAsync(vehicleType.RequiredSpaceUnits, preferredSpotId);

            if (plan == null || plan.Count == 0)
            {
                return AllocationResult.Fail(preferredSpotId != null && preferredSpotId > 0 ?
                    $"The selected spot is not available for {vehicleType.Name} right now."
                    : $"No available spot(s) for {vehicleType.Name} right now.");
            }

            // Get the retry execution strategy for the current context
            var strategy = _context.Database.CreateExecutionStrategy();

            try
            {
                return await strategy.ExecuteAsync(async () =>
                {
                    // Start and manage transactions within the strategy.
                    using var transaction = await _context.Database.BeginTransactionAsync();

                    var session = new ParkingSession
                    {
                        VehicleId = vehicleId,
                        ParkingSpotId = plan[0].SpotId,
                        ArriveTime = DateTime.UtcNow,
                        HourlyRateAtCheckIn = hourlyRate
                    };

                    _context.ParkingSessions.Add(session);
                    await _context.SaveChangesAsync();

                    var allocations = plan
                        .Select(p => new ParkingAllocation
                        {
                            ParkingSessionId = session.Id,
                            ParkingSpotId = p.SpotId,
                            UnitsUsed = p.UnitsUsed
                        })
                        .ToList();

                    _context.ParkingAllocations.AddRange(allocations);
                    await _context.SaveChangesAsync();

                    await transaction.CommitAsync();
                    return AllocationResult.Ok(session, allocations);
                });
            }
            catch (Exception ex)
            {
                return AllocationResult.Fail($"Could not save the parking session: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        public async Task ReleaseAllocationsAsync(int parkingSessionId)
        {
            await Task.CompletedTask;
        }

        public async Task<bool> CanAllocateAsync(int vehicleTypeId)
        {
            var vehicleType = await _context.VehicleTypes.FindAsync(vehicleTypeId);
            if (vehicleType == null) return false;

            var plan = await BuildAllocationPlanAsync(vehicleType.RequiredSpaceUnits, preferredSpotId: null);
            return plan != null;
        }

        private async Task<List<(int SpotId, int UnitsUsed)>?> BuildAllocationPlanAsync(int requiredUnits, int? preferredSpotId)
        {
            var spots = await _context.ParkingSpots
                .Where(s => !s.IsOutOfService)
                .OrderBy(s => s.Number)
                .ToListAsync();

            if (spots.Count == 0) return null;

            var usedUnitsBySpot = await _context.ParkingAllocations
                .Where(a => a.ParkingSession != null && a.ParkingSession.CheckOutTime == null)
                .GroupBy(a => a.ParkingSpotId)
                .Select(g => new { SpotId = g.Key, Used = g.Sum(a => a.UnitsUsed) })
                .ToDictionaryAsync(x => x.SpotId, x => x.Used);

            int FreeUnits(ParkingSpot spot) =>
                spot.CapacityUnits - usedUnitsBySpot.GetValueOrDefault(spot.Id, 0);

            //if (preferredSpotId != null)
            //{
            //    var preferred = spots.FirstOrDefault(s => s.Id == preferredSpotId);
            //    if (preferred != null && requiredUnits <= preferred.CapacityUnits && FreeUnits(preferred) >= requiredUnits)
            //    {
            //        return new List<(int, int)> { (preferred.Id, requiredUnits) };
            //    }
            //}

            // In Park.cshtml, if the preferred spot is not available, let user choose another spot.
            if (preferredSpotId != null)
            {
                var preferred = spots.FirstOrDefault(s => s.Id == preferredSpotId);
                if (preferred is null) return null;

                if (requiredUnits <= preferred.CapacityUnits)
                {
                    if (FreeUnits(preferred) >= requiredUnits)
                    {
                        return new List<(int, int)> { (preferred.Id, requiredUnits) };
                    }
                    return null;
                }

                // When RequirdSpots > 1, it is to say that requiredUnits = 6 or 9.
                int index = spots.IndexOf(preferred);
                int spotsNeeded = requiredUnits == 9 ? 3 : 2;

                if (index != -1 && index + spotsNeeded <= spots.Count)
                {
                    var targetSpots = spots.GetRange(index, spotsNeeded);
                    if (targetSpots.All(s => FreeUnits(s) == s.CapacityUnits))
                    {
                        return targetSpots.Select(s => (s.Id, s.CapacityUnits)).ToList();
                    }
                }

                return null;
            }

            var singleSpot = spots.FirstOrDefault(s =>
                requiredUnits <= s.CapacityUnits && FreeUnits(s) >= requiredUnits);

            if (singleSpot != null)
            {
                return new List<(int, int)> { (singleSpot.Id, requiredUnits) };
            }

            for (int startIndex = 0; startIndex < spots.Count; startIndex++)
            {
                if (FreeUnits(spots[startIndex]) != spots[startIndex].CapacityUnits)
                {
                    continue;
                }

                var window = new List<ParkingSpot> { spots[startIndex] };
                int coveredUnits = spots[startIndex].CapacityUnits;
                int i = startIndex;

                while (coveredUnits < requiredUnits && i + 1 < spots.Count
                       && spots[i + 1].Number == spots[i].Number + 1
                       && FreeUnits(spots[i + 1]) == spots[i + 1].CapacityUnits)
                {
                    i++;
                    window.Add(spots[i]);
                    coveredUnits += spots[i].CapacityUnits;
                }

                if (coveredUnits >= requiredUnits)
                {
                    return window.Select(s => (s.Id, s.CapacityUnits)).ToList();
                }
            }

            return null;
        }
    }
}