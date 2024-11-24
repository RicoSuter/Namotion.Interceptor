# Namotion.Proxy for .NET

Namotion.Proxy is a .NET library designed to simplify the creation of trackable object models by automatically generating property interceptors. All you need to do is annotate your model classes with a few simple attributes; they remain regular POCOs otherwise. The library uses source generation to handle the interception logic for you.

In addition to property tracking, Namotion.Proxy offers advanced features such as automatic change detection (including derived properties), reactive source mapping (e.g., for GraphQL subscriptions or MQTT publishing), and other powerful capabilities that integrate seamlessly into your workflow.

**The library is currently in development and the APIs might change.**

Feature map:

![features](./features.png)
