# RedisQ

A high-performance, Redis-backed job queue library for .NET.

## Overview

RedisQ provides a robust and scalable solution for background job processing in .NET applications. Built on top of Redis, it offers reliable job queuing, processing, and monitoring capabilities with features like job priorities, delays, retries, and real-time progress tracking.

## Proposed Features

- **Redis-Backed**: Leverages Redis for high performance and reliability
- **Job Priorities**: Support for job prioritization
- **Delayed Jobs**: Schedule jobs to run at specific times
- **Job Retries**: Automatic retry mechanism with configurable strategies
- **Dead Letter Queue**: Failed jobs handling
- **Progress Tracking**: Real-time job progress updates
- **Concurrency Control**: Configurable worker concurrency
- **Job Events**: Comprehensive event system for job lifecycle
- **Dashboard Ready**: Built-in support for monitoring and management
- **Type Safety**: Strongly-typed job definitions
- **Horizontal Scaling**: Multiple workers across different processes/machines

## Architecture

RedisQ follows a producer-consumer pattern:

- **Producers** add jobs to queues
- **Workers** (consumers) process jobs from queues
- **Redis** acts as the message broker and persistence layer

## Requirements

- .NET 8.0 or later
- Redis 6.0 or later

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Inspiration

This project is inspired by [BullMQ](https://github.com/taskforcesh/bullmq), bringing similar functionality to the .NET ecosystem with strong typing and .NET conventions.
