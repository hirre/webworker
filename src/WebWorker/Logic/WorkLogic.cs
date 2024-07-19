﻿using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;
using WebWorker.Assembly;
using WebWorker.MessageBroker;
using WebWorker.Models;
using WebWorker.Worker;
using WebWorkerInterfaces;


namespace WebWorker.Logic
{
    public class WorkLogic(IConfiguration configuration,
        IServiceProvider serviceProvider,
        RabbitMQConnectionService rabbitMQConnectionService,
        WorkerRepo workerRepo,
        ILogger<WorkLogic> logger)
    {
        #region Configuration keys

        private const string MAX_WORKERS = "MaxWorkers";
        private const string RABBITMQ_QUEUE_AUTOACK = "RabbitMQ:Queue:AutoAck";
        private const string RABBITMQ_QUEUE_DURABLE = "RabbitMQ:Queue:Durable";
        private const string RABBITMQ_QUEUE_EXCLUSIVE = "RabbitMQ:Queue:Exclusive";
        private const string RABBITMQ_QUEUE_AUTODELETE = "RabbitMQ:Queue:AutoDelete";
        private const string RABBITMQ_QUEUE_ARGUMENTS = "RabbitMQ:Queue:Arguments";
        private const string RABBITMQ_QUEUE_BIND_ARGUMENTS = "RabbitMQ:Queue:BindArguments";
        private const string RABBITMQ_CHANNEL_EXCHANGETYPE = "RabbitMQ:Channel:ExchangeType";
        private const string RABBITMQ_CHANNEL_QOS_PREFETCHSIZE = "RabbitMQ:Channel:Qos:PrefetchSize";
        private const string RABBITMQ_CHANNEL_QOS_PREFETCHCOUNT = "RabbitMQ:Channel:Qos:PrefetchCount";
        private const string RABBITMQ_CHANNEL_QOS_GLOBAL = "RabbitMQ:Channel:Qos:Global";

        #endregion

        private readonly IConfiguration _configuration = configuration;
        private readonly IServiceProvider _serviceProvider = serviceProvider;
        private readonly RabbitMQConnectionService _rabbitMQConnectionService = rabbitMQConnectionService;
        private readonly WorkerRepo _workerRepo = workerRepo;
        private readonly ILogger<WorkLogic> _logger = logger;

        public async Task CreateWorker(CreateWorkerRequestDto createWorkerRequestDto)
        {
            var maxWorkers = int.TryParse(_configuration["MAX_WORKERS"], out var maxWorkersVal) ? maxWorkersVal : 400;

            if (_workerRepo.GetWorkerDataCount() + 1 > maxWorkers)
                throw new InvalidOperationException("Maximum number of workers reached.");

            if (_workerRepo.ContainsWorkerData(createWorkerRequestDto.WorkerId))
                throw new DuplicateWaitObjectException($"Worker {createWorkerRequestDto.WorkerId} already exists.");

            var workerService = new WorkerJob(createWorkerRequestDto.WorkerId, _serviceProvider.GetRequiredService<ILogger<WorkerJob>>(),
                        _serviceProvider.GetRequiredService<WebWorkerAssemblyLoadContext>(), new CancellationTokenSource());

            var conn = _rabbitMQConnectionService.GetConnection();
            var channel = conn.CreateModel();
            var queueName = createWorkerRequestDto.WorkerId;
            var exchangeName = "exchange." + queueName;
            var routingKey = "route." + queueName;

            var consumer = InitializeRabbitMQ(channel, exchangeName, queueName, routingKey);

            var wd = new WorkerData(workerService, channel);

            _workerRepo.AddWorkerData(wd);

            workerService.Start();

            var autoAckValue = bool.TryParse(_configuration[RABBITMQ_QUEUE_AUTOACK], out var ackVal) && ackVal;

            channel.BasicConsume(queue: queueName,
                                 autoAck: autoAckValue,
                                 consumer: consumer);

            await Task.Yield();
        }

        public async Task RemoveWorker(string id)
        {
            if (!_workerRepo.ContainsWorkerData(id))
                throw new KeyNotFoundException($"Worker {id} not found.");

            var workerInfo = _workerRepo.GetWorkerData(id) ?? throw new NullReferenceException($"Null worker {id}.");

            await workerInfo.Worker.StopAsync();
        }

        private void Consumer_Received(object? sender, BasicDeliverEventArgs ea)
        {
            var body = ea.Body.ToArray();

            var msgJsonStr = Encoding.UTF8.GetString(body);

            var msg = JsonSerializer.Deserialize<TestMessage>(msgJsonStr);

            if (msg != null)
            {
                var workerData = _workerRepo.GetWorkerData(msg.Id);

                workerData?.Worker.SignalMessageEvent(msg);

                var autoAckValue = bool.TryParse(_configuration[RABBITMQ_QUEUE_AUTOACK], out var ackVal) && ackVal;

                if (!autoAckValue)
                    workerData?.GetChannel.BasicAck(ea.DeliveryTag, false);
            }
        }

        private EventingBasicConsumer InitializeRabbitMQ(IModel channel, string exchangeName, string queueName, string routingKey)
        {
            var durableValue = bool.TryParse(_configuration[RABBITMQ_QUEUE_DURABLE], out var durVal) && durVal;
            var exclusiveValue = bool.TryParse(_configuration[RABBITMQ_QUEUE_EXCLUSIVE], out var exclVal) && exclVal;
            var autoDeleteValue = bool.TryParse(_configuration[RABBITMQ_QUEUE_AUTODELETE], out var autoDelVal) && autoDelVal;
            var exchangeType = string.IsNullOrEmpty(_configuration[RABBITMQ_CHANNEL_EXCHANGETYPE]) ? ExchangeType.Direct : _configuration[RABBITMQ_CHANNEL_EXCHANGETYPE];
            var channelQosPrefetchSize = uint.TryParse(_configuration[RABBITMQ_CHANNEL_QOS_PREFETCHSIZE], out var qosPrefetchSize) ? qosPrefetchSize : 0;
            var channelQosPrefetchCount = ushort.TryParse(_configuration[RABBITMQ_CHANNEL_QOS_PREFETCHCOUNT], out var qosPrefetchCount) ? qosPrefetchCount : (ushort)1;
            var channelQosGlobal = bool.TryParse(_configuration[RABBITMQ_CHANNEL_QOS_GLOBAL], out var qosGlobal) && qosGlobal;

            channel.ExchangeDeclare(exchangeName, exchangeType);

            channel.QueueDeclare(queue: queueName,
                                 durable: durableValue,
                                 exclusive: exclusiveValue,
                                 autoDelete: autoDeleteValue,
                                 arguments: LoadArguments(RABBITMQ_QUEUE_ARGUMENTS));

            channel.BasicQos(prefetchSize: channelQosPrefetchSize, prefetchCount: channelQosPrefetchCount, global: channelQosGlobal);

            channel.QueueBind(queueName, exchangeName, routingKey, LoadArguments(RABBITMQ_QUEUE_BIND_ARGUMENTS));

            var consumer = new EventingBasicConsumer(channel);

            consumer.Received += Consumer_Received;

            return consumer;
        }

        private Dictionary<string, object>? LoadArguments(string sectionName)
        {
            var argsSection = _configuration.GetSection(sectionName);

            var arguments = new Dictionary<string, object>();

            argsSection.GetChildren().AsEnumerable().ToList().ForEach(x =>
            {
                if (x.Value != null)
                {
                    if (bool.TryParse(x.Value, out bool boolValue))
                        arguments.Add(x.Key, boolValue);
                    else if (int.TryParse(x.Value, out int intValue))
                        arguments.Add(x.Key, intValue);
                    else if (double.TryParse(x.Value, out var doubleValue))
                        arguments.Add(x.Key, doubleValue);
                    else
                        arguments.Add(x.Key, x.Value);
                }
            });

            return arguments.Count != 0 ? arguments : null;
        }
    }
}
