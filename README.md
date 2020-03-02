This is the Quartz integration plugin for Akka.NET.


## Using ##
Install:
```
PM>Install-Package Akka.Quartz.Actor
```
Create a Receiver:
```csharp
class Receiver: ActorBase
{
    public Receiver()
    {
    }

    protected override bool Receive(object message)
    {
    	//handle scheduled message here
    }
 }
var receiver = Sys.ActorOf(Props.Create(() => new Receiver()), "Receiver");
```

Create a QuartzActor:
```csharp
var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzActor()), "QuartzActor");
```

Send "Hello" message to Receiver Actor:
```csharp
quartzActor.Tell(new CreateJob(receiver, "Hello", TriggerBuilder.Create().WithCronSchedule( " * * * * * ?").Build())));
```

Now message "Hello" will be delivered to receiver every 5 seconds.

## PersistentActor ##
 The persistent quartz scheduling actor. This allows the jobs to be persisted in the Quartz jobstore and then to work in a new instance of application with new incarnations of the actors.

```csharp
var quartzPersistentActor = Sys.ActorOf(Props.Create(() => new QuartzPersistentActor()), "QuartzActor");
quartzPersistentActor.Tell(new CreatePersistentJob(receiver, "Hello", TriggerBuilder.Create().WithCronSchedule("*0/10 * * * * ?").Build()));
```

For more information, please see the unit test.

For more information about quartz scheduler please see
http://www.quartz-scheduler.net/documentation/

For more information about akka.net please see
https://getakka.net/articles/intro/what-is-akka.html
