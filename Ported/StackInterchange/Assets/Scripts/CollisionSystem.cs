﻿using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;
using UnityEngine.Networking;

[UpdateAfter(typeof(CarInitializeSystem))]
public class CollisionSystem : SystemBase
{
    private const float BrakingFactor = 0.9f;
    private const float AccelerationFactor = 1.0f / BrakingFactor;
    
    private EntityQuery m_CarQuery;
    private EntityQuery m_SplineQuery;
    
    private struct SegmentCar
    {
        public int segmentIndex;
        public Entity carEntity;
        //public float carSpeed;
        public float3 carNextPosition;
        public float carExtent;
    }

    private struct CarRange
    {
        public int Offset;
        public int Count;
    }

    private struct SortBySegment : IComparer<SegmentCar>
    {
        public int Compare(SegmentCar x, SegmentCar y)
        {
            if (x.segmentIndex < y.segmentIndex) return -1;
            if (y.segmentIndex < x.segmentIndex) return 1;
            if (x.carEntity.Index < y.carEntity.Index) return -1;
            if (y.carEntity.Index < x.carEntity.Index) return 1;
            return 0;
        }
    }

    protected override void OnCreate()
    {
        m_CarQuery = GetEntityQuery(new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<BelongToSpline>()
            }
        });

        m_SplineQuery = GetEntityQuery(new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<Spline>()
            }
        });
    }

    // TODO: This should be a generic helper in one place
public static float3 CalculateCarPosition(in SegmentCollection segmentCollection, 
    in CurrentSegment currentSegment, in Progress progress, in Offset offset)
{
    var segment = segmentCollection.Value.Value.Segments[currentSegment.Value];
    var position = math.lerp(segment.Start, segment.End, progress.Value);

    //1. Compute from source segment data
    var forward = segment.End - segment.Start;
    var up = new float3(0.0f, 1.0f, 0.0f);
    var left = math.normalize(math.cross(up, forward));

    //2. Slower or equivalent
    //var left = segmentCollection.Value.Value.SegmentsLeft[currentSegment.Value];

    position += offset.Value * left;
    return position;
}
    // TODO: This should be a generic helper in one place
    // TODO: Offset should be taken into account
    public static float3 CalculateCarDirection(in SegmentCollection segmentCollection,
        in CurrentSegment currentSegment, in Progress progress, in Offset offset)
    {

        //1. Compute from source segment data
        var segment = segmentCollection.Value.Value.Segments[currentSegment.Value];
        var forward = segment.End - segment.Start;

        //2. Useless in this context
        //var forward = segmentCollection.Value.Value.SegmentsForward[currentSegment.Value];

        return forward;
    }

    protected override void OnUpdate()
    {
        int numberOfCars = m_CarQuery.CalculateEntityCount();
        if (numberOfCars == 0) return; // This may be the case in the beginning
        
        var segmentCollection = GetSingleton<SegmentCollection>();

        int numberOfSegments = segmentCollection.Value.Value.Segments.Length;
        if (numberOfSegments == 0) return;

        var deltaTime = Time.DeltaTime;
        
        // 1. Sort cars by segment in one big array
        NativeArray<SegmentCar> carList = new NativeArray<SegmentCar>(numberOfCars, Allocator.TempJob);
        
        Entities
            .WithName("ListAllCarsAndCalculateNextPosition")
            .ForEach((
                int entityInQueryIndex,
                in Entity entity,
                in CurrentSegment currentSegment,
                in Speed speed,
                in Progress progress,
                in Offset offset,
                in Size size) =>
            {
                var position = CalculateCarPosition(segmentCollection, currentSegment, progress, offset);
                var direction = CalculateCarDirection(segmentCollection, currentSegment, progress, offset);
                var extent = math.length(size.Value);

                var step = speed.Value * deltaTime;
                var nextPosition = position + step * direction;
                
                carList[entityInQueryIndex] = new SegmentCar
                {
                    carEntity = entity,
                    segmentIndex = currentSegment.Value,
                    carNextPosition = nextPosition,
                    carExtent = extent
                };    
            }).ScheduleParallel();

        // Using Jobs to utilize implicit dependencies
        Job
            .WithName("SortCarsBySegment")
            .WithCode(() =>
        {
            var sortBySegment = new SortBySegment();
            carList.Sort(sortBySegment);
        }).Schedule();
        
        // 2. Find the start offset and number of cars for each segment in the array
        NativeArray<CarRange> carsRangePerSegment = new NativeArray<CarRange>(numberOfSegments, Allocator.TempJob);

        // Using Jobs to utilize implicit dependencies
        Job
            .WithName("FindSegmentCarsInArray")
            .WithCode(() =>
        {
            int currentCarIndex = 0;
            for (int i = 0; i < numberOfSegments; ++i)
            {
                int start = currentCarIndex;
                int count = 0;
                while (currentCarIndex < numberOfCars && carList[currentCarIndex].segmentIndex == i)
                {
                    count++;
                    currentCarIndex++;
                }

                carsRangePerSegment[i] = new CarRange {Count = count, Offset = start};
                //if (count > 0)
                    //Debug.Log("Segment " + i + " has " + count + " cars");
            }
        }).Schedule();

        var splineDataArray = m_SplineQuery.ToComponentDataArray<Spline>(Allocator.TempJob);

        // 3. For each car find the collisions with the cars in the same segment
        Entities
            .WithName("DetectAndAvoidCollisions")
            .WithDisposeOnCompletion(carsRangePerSegment)
            .WithDisposeOnCompletion(carList)
            .WithDisposeOnCompletion(splineDataArray)
            .ForEach((
                ref Speed speed,
                in OriginalSpeed originalSpeed,
                in Entity thisCarEntity,
                in Size size,
                in Offset offset,
                in CurrentSegment currentSegment,
                in Progress progress) =>
            {
                var position = CalculateCarPosition(segmentCollection, currentSegment, progress, offset);
                var direction = CalculateCarDirection(segmentCollection, currentSegment, progress, offset);
                var extent = math.length(size.Value);

                var step = speed.Value * deltaTime;
                var nextPosition = position + step * direction;

                bool willCollide = false;
                float minDistance = 1e6f; // Big number
                
                // We test against a few cars nearby (this segment and the next one) instead of all of them
                CarRange carsToTest = carsRangePerSegment[currentSegment.Value];
                if (carsToTest.Count > 1) // Cannot collide, if there is only one car in the current segment
                {
                    for (int i = carsToTest.Offset; i < carsToTest.Offset + carsToTest.Count; ++i)
                    {
                        Entity theOtherCarEntity = carList[i].carEntity;
                        if (theOtherCarEntity == thisCarEntity) continue; // Don't compare the car against itself

                        //Debug.Log("Checking car " + thisCarEntity + " against " + theOtherCarEntity);

                        var theOtherCarNextPosition = carList[i].carNextPosition;
                        var theOtherCarExtent = carList[i].carExtent;

                        // Simply assume that if the car is in front of as and the next positions will be closer than the
                        // car maximum extents together, then there can be a collision.
                        float3 diff = theOtherCarNextPosition - nextPosition;
                        bool theOtherCarIsInFront = math.dot(direction, diff) > 0;

                        // The car in the back should do the braking, not the front one
                        if (theOtherCarIsInFront)
                        {
                            float distance = math.length(diff);
                            if (distance < minDistance)
                            {
                                minDistance = distance;
                            }

                            // Heuristics to detect an imminent collision
                            float safetyDistance = extent * 2 + theOtherCarExtent;
                            if (distance < safetyDistance)
                            {
                                willCollide = true;
                            }
                        }
                    }
                }

                // Get the next segment too
                var spline = GetComponent<BelongToSpline>(thisCarEntity);
                var splineData = splineDataArray[spline.Value];
                var segmentCounter = GetComponent<SegmentCounter>(thisCarEntity);
                if (segmentCounter.Value < splineData.Value.Value.Segments.Length - 1)
                {
                    carsToTest = carsRangePerSegment[splineData.Value.Value.Segments[segmentCounter.Value + 1]];
                    
                    for (int i = carsToTest.Offset; i < carsToTest.Offset + carsToTest.Count; ++i)
                    {
                        Entity theOtherCarEntity = carList[i].carEntity;
                        if (theOtherCarEntity == thisCarEntity) continue; // Don't compare the car against itself

                        //Debug.Log("Checking car " + thisCarEntity + " against " + theOtherCarEntity);

                        var theOtherCarNextPosition = carList[i].carNextPosition;
                        var theOtherCarExtent = carList[i].carExtent;

                        // Simply assume that if the car is in front of as and the next positions will be closer than the
                        // car maximum extents together, then there can be a collision.
                        float3 diff = theOtherCarNextPosition - nextPosition;
                        bool theOtherCarIsInFront = math.dot(direction, diff) > 0;

                        // The car in the back should do the braking, not the front one
                        if (theOtherCarIsInFront)
                        {
                            float distance = math.length(diff);
                            if (distance < minDistance)
                            {
                                minDistance = distance;
                            }

                            // Heuristics to detect an imminent collision
                            float safetyDistance = extent * 2 + theOtherCarExtent;
                            if (distance < safetyDistance)
                            {
                                willCollide = true;
                            }
                        }
                    }
                }

                if (willCollide)
                {
                    //Debug.Log("Car " + thisCarEntity + " will collide soon.");
                    
                    // Avoid collision by slowing down
                    float newSpeed = math.max(0.0f, speed.Value - originalSpeed.Value * 0.1f);
                    speed.Value = newSpeed;
                }
                else
                {
                    // Heuristics to detect the situation, when the road is clear and we could accelerate to the original speed.
                    if (minDistance > extent * 3)
                    {
                        if (speed.Value < originalSpeed.Value)
                        {
                            //Debug.Log("Car " + thisCarEntity + " accelerating back to original speed");
                            
                            float newSpeed = math.min(originalSpeed.Value, speed.Value + originalSpeed.Value * 0.1f);
                            speed.Value = newSpeed;
                        }
                    }
                }
            }).ScheduleParallel();

        //carsRangePerSegment.Dispose();
        //carList.Dispose();
    }
}
