var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Namotion_Interceptor_OpcUa_SampleClient>("namotion-interceptor-opcua-sampleclient");

builder.AddProject<Projects.Namotion_Interceptor_OpcUa_SampleServer>("namotion-interceptor-opcua-sampleserver");

builder.Build().Run();
