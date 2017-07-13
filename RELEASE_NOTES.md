#### 1.2.3 July 13 2017 ####
Updated Akka to 1.2.3

#### 1.2.0 February 18 2017 ####
Updated Akka to 1.2.0, along with a number of other dependencies including Newtonsoft and Entity Framework.

#### 1.0.4 February 21 2017 ####
Created the `JobNotFoundException` type. If a job is not found when invoking the `RemoveJob` command, 
`RemoveJobFail` will contain an exception of this type instead of the string "job not found".


#### 1.0.3 Januari 26 2017 ####
updated to akka 1.1.3  
Serializing the ActorPath and message to save in a persistent Quartz JobStore  
Add the capability for the QuartzActor and the QuartzPersistentActor to take a scheduler rather than creating one.

#### 1.0.2 September 23 2016 ####
added quartz persistent actor, quartz persistent job and create persistent job message
fix properties setters
updated to akka 1.1.2