﻿using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

public class BucketRefillUpdateSystem : SystemBase
{
    
    protected override void OnUpdate()
    {
        Entities
        .WithName("bucket_refill_lake")
        .WithAll<WaterRefill>()
        .ForEach((ref WaterAmount lakeWaterAmount) =>
        {
            // hardcoded value for capacity 1000
            // hardcoded refillRate 1
            var capacity = 1000.0f;
            var refillRate = 1.0f;

            if(lakeWaterAmount.Value < capacity)
            {
                lakeWaterAmount.Value += refillRate;
            }
        }).ScheduleParallel();
    }
}

