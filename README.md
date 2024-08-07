# Dynamic Web Worker

A worker service that can dynamically be loaded with a number of worker threads using REST calls. The workers can (based on incoming messages) process some work which are located in separate libraries that are also loaded dynamically.

## RabbitMQ Installation & Running with Docker
```
docker pull rabbitmq:3-management
docker run -d --name rabbitmq -p 5672:5672 -p 15672:15672 rabbitmq:3-management
```

## Code Structure
- **WebWorker** is the main worker service
  - Dynamically loaded work libraries are put as subfolders in /Work/* (they can be built with the publish command)
- **WebWorkerInterfaces** is library with shared interfaces and models
- **TestMessageProducer** is a test library to produce RabbitMQ messages
- **TestWork** is a test library which implements an interface that the WebWorker dynamically loads and runs
  - Should be built with publish command and put as a subfolder in the /Work folder
 
## Architecture 

### Overview
![image](https://github.com/user-attachments/assets/183d6cff-18d3-4c33-8187-3d18dfc31a0f)

### Type of queue used for permanent workers
![image](https://github.com/user-attachments/assets/88ee5d20-51fb-406d-9b77-ff6ea73538f5)

### Other
- Can execute custom work code without using the dynamic loading feature: https://github.com/hirre/dynamicwebworker/issues/8#issuecomment-2256109699
