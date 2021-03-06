﻿#define BINARY_HEAP	//Using a binary heap: much better
#if (!BINARY_HEAP)
#define REVERSE_QUEUE	//Event queue using a reversed list (better than normal list, but not as good as binary heap)
#endif

//#define VERBOSE_DEBUG	//Additional information in DEBUG mode

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using SimManning.Domain;

namespace SimManning.Simulation
{
	/// <summary>
	/// Event-based discrete event simulation engine.
	/// </summary>
	public sealed class Simulator
	{
		Random rand;

		readonly SimulationDataSet simulationDataSet;

		public SimulationDataSet SimulationDataSet
		{
			get { return this.simulationDataSet; }
		}

		/// <summary>
		/// Used for NoDuplicate and Slaves.
		/// </summary>
		int[] taskIdsActive;

		int nbObligatoryTasksActive;

		SimulationTime simulationTime;

		Phase simulationPhase;

		int nbEventInsertions;

		DomainDispatcher dispatcher;

		/// <summary>
		/// Count the number of event insertions to get some metrics about the performance of the logic.
		/// </summary>
		public int NbEventInsertions
		{
			get { return this.nbEventInsertions; }
		}

		#region Events
		public delegate void PhaseTransitionEventHandler(Phase previousPhase, Phase nextPhase, SimulationTime time);
		public event PhaseTransitionEventHandler OnPhaseTransitionBegin;
		public event PhaseTransitionEventHandler OnPhaseTransitionEnd;

		public delegate void TaskErrorEventHandler(Phase phase, SimulationTask task);
		public event TaskErrorEventHandler OnTaskError;

		public delegate void ErrorMessageEventHandler(string text);
		public event ErrorMessageEventHandler OnErrorMessage;
		#endregion

		public Simulator(SimulationDataSet simulationDataSet)
		{
			this.simulationDataSet = simulationDataSet;
			this.simulationDataSet.PrepareForFirstSimulation();
			this.taskIdsActive = new int[this.simulationDataSet.TaskDictionaryExpanded.Count + 1];
			this.OnErrorMessage += (text) => Debug.WriteLine(text);
		}

		/*public static Version Version
		{
			get { return typeof(Simulator).Assembly.GetName().Version; }	//Difficult to target multiple .NET versions such as .NET 4.0, .NET 4.5, and Windows 8
		}*/

		void PrepareForNextReplication()
		{
			this.taskIdsActive.Clear();
			this.eventQueue.Clear();
			this.nbEventInsertions = 0;
			this.nbObligatoryTasksActive = 0;
			this.simulationDataSet.PrepareForNextReplication();
			{
				var myMessage = this.simulationDataSet.ErrorMessage;
				if ((!String.IsNullOrEmpty(myMessage)) && (this.OnErrorMessage != null))
					this.OnErrorMessage(myMessage);
				myMessage = this.simulationDataSet.WarningMessage;
				if ((!String.IsNullOrEmpty(myMessage)) && (this.OnErrorMessage != null))
					this.OnErrorMessage(myMessage);
			}
			foreach (var task in this.simulationDataSet.TaskDictionaryExpanded.Values.Where(t => t.Enabled))
				if (!task.Valid)
				{
					if (this.OnTaskError != null) this.OnTaskError(phase: null, task: task);	//Event
					task.Enabled = false;	//Disable invalid tasks
				}
				else if (task.DateOffset.Unit != TimeUnit.Undefined)
					task.DateOffset.NextValue(this.rand);	//Task initialisation
			foreach (var task in this.simulationDataSet.TaskDictionaryExpanded.Values.Where(t => (!t.IsPhaseDependent) && (!t.AutoExpandToAllCrewmen) && t.Enabled))
			{//Enqueue phase-independent tasks
				SimulationTime nextEventTime;
				if (task.DateOffset.Unit == TimeUnit.Undefined)
					switch (task.TaskTypeSubCode1)	//Automatic offset
					{
						case (int)StandardTaskType.ExternalCondition:
						case (int)StandardTaskType.CriticalEvents:
							nextEventTime = this.simulationTime + (task.StartDate.NextValue(this.rand) / 2);	//Add half of the frequency for the first occurence of critical events and external conditions?
							break;
						default:
							nextEventTime = this.simulationTime;	//Start the first occurence immediatly for other types of task
							break;
					}
				else nextEventTime = this.simulationTime;	//Explicit offset will be done in EnqueueTaskArrival
				EnqueueTaskArrival(this.simulationDataSet.TaskDictionaryExpanded.CreateTask(task, TaskLinkingType.Linked), nextEventTime, original: true);
			}
		}

		public bool Run(SimulationTime timeOrigin, DomainDispatcher domainDispatcher, Random aRand = null)
		{
			Debug.WriteLine("{0}======== {1} ========", Environment.NewLine, this.simulationDataSet);
			this.simulationTime = timeOrigin;
			var timeLimit = timeOrigin;
			this.dispatcher = domainDispatcher;
			this.rand = aRand ?? new Random();
			PrepareForNextReplication();
			Phase previousPhase = null;
			foreach (var phase in this.simulationDataSet.Scenario.Phases)
			{
				phase.Duration.NextValue(this.rand);
				Debug.WriteLine("{0}==== {1} {2}.{3} ===={0}", Environment.NewLine, this.simulationTime, phase.Id, phase.Name);
				DoPhaseTransition(previousPhase, phase);
				SimulationTaskEvent myTaskEvent;
				while ((this.eventQueue.Count > 0) && ((myTaskEvent = PeekEvent()).EventTime <= timeLimit) &&
					((myTaskEvent.subtype & SimulationTaskEvent.SubtypeType.TaskMetaStop) == myTaskEvent.subtype))
					ConsumeEvent(RemoveEvent());	//Consume all events from the previous phase, which should stop now
				this.simulationPhase = phase;
				phase.simulationTimeBegin = this.simulationTime;
				if (phase.Duration.XValue.Positive)
				{//Duration of the phase, represented by an obligatory task of the same length.
					var durationTask = this.simulationDataSet.TaskDictionaryExpanded.CreateTask(-phase.Id);
					durationTask.InternalId = 0;
					durationTask.TaskType = (int)StandardTaskType.InternalWait;
					durationTask.Name = String.Format(CultureInfo.InvariantCulture, "!Duration task for phase {0} ({1})", phase.Id, phase.Name);
					durationTask.TaskInterruptionPolicy = TaskInterruptionPolicies.DropWithError;
					durationTask.PhaseInterruptionPolicy = PhaseInterruptionPolicies.Obligatory;
					durationTask.ScenarioInterruptionPolicy = ScenarioInterruptionPolicies.DropWithError;
					this.nbObligatoryTasksActive++;
					durationTask.RelativeDate = RelativeDateType.RelativeStartFromStartOfPhase;
					durationTask.StartDate = TimeDistribution.Zero;
					durationTask.DateOffset = TimeDistribution.Zero;
					durationTask.Duration = phase.Duration;
					durationTask.OnHolidays = true;
					durationTask.NumberOfCrewmenNeeded = 0;
					durationTask.SimulationTimeArrives = this.simulationTime;
					durationTask.PrepareForNextOccurrence();
					//if (phase.Tasks.ContainsKey(durationTask.Id)) phase.Tasks.Remove(durationTask.Id);
					//phase.Tasks.Add(durationTask.Id, durationTask);
					this.taskIdsActive[durationTask.InternalId]++;
					AddEvent(new SimulationTaskEvent(durationTask, this.simulationTime + (phase.Duration.XValue > Phase.ArbitraryMaxDuration ? Phase.ArbitraryMaxDuration : phase.Duration.XValue), SimulationTaskEvent.SubtypeType.TaskEnds));
				}
				foreach (var task in this.simulationDataSet.TaskDictionaryExpanded.Values.Where(t => t.IsPhaseDependent && (!t.AutoExpandToAllCrewmen) &&	//Candidate for .AsParallel()
					t.Enabled && t.PhaseTypes.Any(pt => phase.PhaseType.IsSubCodeOf(pt))))
					EnqueueTaskArrival(this.simulationDataSet.TaskDictionaryExpanded.CreateTask(task, TaskLinkingType.Linked), this.simulationTime, original: true);	//Enqueue tasks for phases of this type
				foreach (var task in phase.Tasks.Values.Where(t => t.IsPhaseDependent && (!t.AutoExpandToAllCrewmen) && t.Enabled))
					EnqueueTaskArrival(task, this.simulationTime, original: true);	//Enqueue tasks for this specific phase
				timeLimit = this.simulationTime;
				var i = 0;
				while ((this.eventQueue.Count > 0) && (this.nbObligatoryTasksActive > 0) &&
					((++i % 256 != 0) || (this.simulationTime - phase.simulationTimeBegin <= Phase.ArbitraryMaxDuration)))	//Test for max phase duration only once in a while
					ConsumeEvent(RemoveEvent());
				Debug.Assert((this.eventQueue.Count <= 0) || (PeekEvent().EventTime >= this.simulationTime), "No event should remain in the past!");
				timeLimit = this.simulationTime;
				while ((this.eventQueue.Count > 0) && ((myTaskEvent = PeekEvent()).EventTime <= timeLimit) &&
					((myTaskEvent.subtype & SimulationTaskEvent.SubtypeType.TaskMetaStop) == myTaskEvent.subtype))
					ConsumeEvent(RemoveEvent());	//Consume all event that should stop now
				previousPhase = phase;
			}
			Debug.WriteLine("{0}==== {1} End of scenario ({2} events) ===={0}", Environment.NewLine, this.simulationTime, this.nbEventInsertions);
			DoPhaseTransition(previousPhase, null);	//Ends the simulation
			Debug.Assert(!this.simulationDataSet.Crew.Values.Any(cm => cm.TasksAssigned.Count > 0), "No crewman should have remaining tasks assigned at the end of a scenario!");
			this.dispatcher = null;
			return true;
		}

		void DoPhaseTransitionOfActiveTask(SimulationTask task, SimulationTime time, Phase previousPhase, Phase phase, SimulationTaskEvent myEvent, ref bool killed, ref bool error)
		{
			if (myEvent.subtype == SimulationTaskEvent.SubtypeType.TaskAwakes)
			{//Resume tasks coming from e.g. ResumeAndComplete that are hibernated
				myEvent.EventTime = myEvent.Task.NextPossibleResume(time);
				task.SleepUntilNow(myEvent.EventTime);
			}
			else if ((myEvent.subtype & SimulationTaskEvent.SubtypeType.TaskMetaStop) == myEvent.subtype)
				task.ProcessUntilNow(time, previousPhase);
			switch (task.PhaseInterruptionPolicy)
			{
				case PhaseInterruptionPolicies.ResumeOrDropWithoutError:
					error = false;
					goto case PhaseInterruptionPolicies.ResumeOrDropWithError;	//C# style switch case fall-through
				case PhaseInterruptionPolicies.ResumeOrDropWithError:
					if (phase == null) error = task.ScenarioInterruptionPolicy == ScenarioInterruptionPolicies.DropWithError;
					else
					{//Task always continues except at the end of the scenario
						killed = false;
						if (!task.PhaseTypes.Any(pt => phase.PhaseType.IsSubCodeOf(pt)))
						{//Hibernate tasks that are not allowed to run in next phase
							if ((myEvent.subtype & SimulationTaskEvent.SubtypeType.TaskMetaStart) == myEvent.subtype)
								task.SleepUntilNow(time);	//For correct ProcessUntilNow of when dealing with TaskHibernated event
							myEvent.subtype = SimulationTaskEvent.SubtypeType.TaskHibernated;
							myEvent.EventTime = time;
							if (myEvent.Task.SlaveTasks.Count > 0)
								StopSlaves(myEvent, sort: false);	//"sort: false" because we need to maintain order
						}
					}
					break;
				case PhaseInterruptionPolicies.ContinueOrDropWithoutError:
					error = false;
					goto case PhaseInterruptionPolicies.ContinueOrDropWithError;
				case PhaseInterruptionPolicies.ContinueOrDropWithError:
					//case Task.PhaseInterruptionPolicies.DoNotInterrupt:
					if (phase == null) error = (task.ScenarioInterruptionPolicy == ScenarioInterruptionPolicies.DropWithError);
					else if (task.PhaseTypes.Any(pt => phase.PhaseType.IsSubCodeOf(pt)))
						killed = false;	//Task can continue
					break;
				case PhaseInterruptionPolicies.DropWithoutError:
				case PhaseInterruptionPolicies.WholePhase:
					error = false;
					break;
				case PhaseInterruptionPolicies.Obligatory:
				case PhaseInterruptionPolicies.DropWithError:
				default:
					break;
			}
		}

		void DoPhaseTransition(Phase previousPhase, Phase phase)
		{//TODO: Improve maintenability index to be >= 30 also in debug mode (see Code Metrics)
			if (this.OnPhaseTransitionBegin != null)	//Event
				this.OnPhaseTransitionBegin(previousPhase, phase, this.simulationTime);
			this.taskIdsActive.Clear();
			this.nbObligatoryTasksActive = 0;
			foreach (var myEvent in this.asapQueue)
				myEvent.EventTime = myEvent.Task.NextPossibleResume(this.simulationTime);
			foreach (var myEvent in this.parallelQueue.Values)
				myEvent.EventTime = myEvent.Task.NextPossibleResume(this.simulationTime);
			var eventsToKeep = new List<SimulationTaskEvent>();
			var oldParallelQueue = this.parallelQueue.Values.ToList();
			this.parallelQueue.Clear();
			foreach (var myEvent in this.asapQueue.Concat(this.eventQueue).Concat(oldParallelQueue))
			{//The events may be processed in a random order. Good potential for parallel processing
				var task = myEvent.Task;
				var killed = true;
				var error = true;
				if (myEvent.subtype == SimulationTaskEvent.SubtypeType.TaskPlanned || myEvent.subtype == SimulationTaskEvent.SubtypeType.TaskWaitingParallel)
				{
					error = false;	//The task had not arrived yet
					if (phase != null)
						switch (task.RelativeDate)
						{
							case RelativeDateType.Frequency:
								killed = false;	//Task can continue
								break;
							case RelativeDateType.RelativeStartFromPreviousStart:
								//Temporarily keep tasks that are relative to the previous occurence if they appear in the next phase
								if (task.PhaseTypes.Any(pt => phase.PhaseType.IsSubCodeOf(pt)) &&
									(!eventsToKeep.Concat(parallelQueue.Values).Any(e => e.Task.Id == task.Id)))	//Keep only one instance	//TODO: Debug
									killed = false;	//Task can temporarily continue
								break;
						}
				}
				else if (task.RelativeDate == RelativeDateType.TriggeredByAnEvent)
				{
					killed = false;	//Slaves will be killed indirectly
					myEvent.Task.ProcessUntilNow(this.simulationTime, previousPhase);
				}
				else if (myEvent.subtype == SimulationTaskEvent.SubtypeType.TaskForNextPhase)
				{
					error = false;
					if (phase != null)
					{
						killed = false;
						if (task.PhaseTypes.Any(pt => phase.PhaseType.IsSubCodeOf(pt)))
						{
							myEvent.subtype = SimulationTaskEvent.SubtypeType.TaskPlanned;
							myEvent.EventTime = myEvent.Task.NextPossibleResume(this.simulationTime);
						}
					}
				}
				else DoPhaseTransitionOfActiveTask(task, this.simulationTime, previousPhase, phase, myEvent, ref killed, ref error);
				if (killed || (phase == null))
				{
					if (error)
					{
						Debug.WriteLine("☒\tKill {0}\t(lacking: {1})", myEvent, myEvent.Task.RemainingDuration);
						if (this.OnTaskError != null) this.OnTaskError(previousPhase, task);	//Event
					}
					else if (myEvent.subtype == SimulationTaskEvent.SubtypeType.TaskPlanned)
						Debug.WriteLine("■\tAbandon {0}", myEvent);	//The task had not arrived yet
					else Debug.WriteLine("■\tKill {0}\t(lacking: {1})", myEvent, myEvent.Task.RemainingDuration);
					this.dispatcher.TaskDismiss(this.simulationTime, previousPhase, myEvent.Task);
					if (myEvent.Task.SlaveTasks.Count > 0)
						StopSlaves(myEvent, sort: false);	//"sort: false" because we need to maintain order
					//if (myEvent.Task.ParallelTasks.Count > 0) InterruptParallelTasks(myEvent, sort: false);	//TODO: Currently improper
					/*switch (task.RelativeDate)
					{
						case Task.RelativeDateType.RelativeStartFromPreviousStartOccurrence:
							//Mummify tasks relative to the previous occurrence, to keep their starting time as a reference
							if (!eventsToKeep.Any(e => e.Task.Id == task.Id))
							{//Keep only one instance
								myEvent.EventSubType = SimulationTaskEvent.SubType.TaskMummified;
								myEvent.Time = SimulationTime.MaxValue;	//Task will be removed by upcoming tasks of the same ID
								eventsToKeep.Add(myEvent);
							}
							break;
					}*/
				}
				else
				{
					if (myEvent.Task.PhaseInterruptionPolicy == PhaseInterruptionPolicies.Obligatory)
						this.nbObligatoryTasksActive++;
					if (myEvent.subtype == SimulationTaskEvent.SubtypeType.TaskWaitingParallel)
						this.parallelQueue.Add(myEvent.Task.Id, myEvent);
					else eventsToKeep.Add(myEvent);
					if ((myEvent.subtype & (SimulationTaskEvent.SubtypeType.TaskPlanned | SimulationTaskEvent.SubtypeType.TaskAwakes)) != myEvent.subtype)
						this.taskIdsActive[myEvent.Task.InternalId]++;
				}
			}
			this.asapQueue.Clear();
			this.eventQueue.Clear();
			this.eventQueue.AddRange(eventsToKeep);
			this.eventQueue.TrimExcess();
			this.eventQueue.Sort();
			foreach (var crewman in this.simulationDataSet.Crew.Values)
				crewman.RefreshStatus(this.simulationTime);
			if (this.OnPhaseTransitionEnd != null)	//Event
				this.OnPhaseTransitionEnd(previousPhase, phase, this.simulationTime);
		}

		/// <summary>
		/// Sub-function of <see cref="ConsumeEvent"/>.
		/// </summary>
		/// <param name="simulationEvent">The simulation event processed,
		/// which type is <see cref="SimulationTaskEvent.SubtypeType.TaskPlanned"/>.</param>
		/// <returns>true if the task was indeed a duplicate, false otherwise</returns>
		bool ConsumeTaskDuplicate(SimulationTaskEvent simulationEvent)
		{
			var currentTask = simulationEvent.Task;
			SimulationTaskEvent duplicateEvent;
			int i;
			if ((simulationEvent.Task.ParallelTasks.Count > 0) && this.parallelQueue.TryGetValue(currentTask.Id, out duplicateEvent))
				this.parallelQueue.Remove(currentTask.Id);
			else if ((i = FindNextAsapIndex(currentTask.Id)) >= 0)
			{//Check first for duplicates in the ASAP queue
				duplicateEvent = this.asapQueue[i];
				if (duplicateEvent.Task.DuplicatesPolicy != TaskDuplicatesPolicy.KillNewDuplicates)
					this.asapQueue.RemoveAt(i);
			}
			else if ((i = FindNextEventIndex(currentTask.Id)) >= 0)
			{
				duplicateEvent = this.eventQueue[i];
				if (duplicateEvent.Task.DuplicatesPolicy != TaskDuplicatesPolicy.KillNewDuplicates)
					this.eventQueue.RemoveAt(i);	//Interrupt only the first instance of a given task, assuming that this is the one that is active (to be refined).
			}
			else
			{
				duplicateEvent = null;
				Debug.Assert(false, "A task ID is active but no corresponding task was found!");
			}
			if (duplicateEvent == null) return false;	//Was not a duplicate
			else
			{
				if ((duplicateEvent.subtype & SimulationTaskEvent.SubtypeType.TaskMetaStart) == duplicateEvent.subtype)	//Task was sleeping
					duplicateEvent.Task.SleepUntilNow(this.simulationTime);	//In order to avoid problems later when ProcessUntilNow will be called when processing TaskKilled
				else duplicateEvent.Task.ProcessUntilNow(this.simulationTime, this.simulationPhase);	//In order to have the good remaining duration
				switch (duplicateEvent.Task.DuplicatesPolicy)
				{
					/*case TaskDuplicatesPolicy.Automatic:
						if ("Number of current instances >= number of qualified crewmen") goto case TaskDuplicatesPolicy.MergeDuplicates;
						else goto case TaskDuplicatesPolicy.KeepDuplicates;*/	//TODO: Implement Automatic taskDuplicatesPolicy
					case TaskDuplicatesPolicy.MergeDuplicates:
						Debug.WriteLine("➨\t{0}\t(Merging with existing tasks: {1})", simulationEvent, duplicateEvent.Task.RemainingDuration);
						currentTask.RemainingDuration += duplicateEvent.Task.RemainingDuration;
						goto case TaskDuplicatesPolicy.KillOldDuplicates;
					case TaskDuplicatesPolicy.KillOldDuplicates:
						if (duplicateEvent.subtype == SimulationTaskEvent.SubtypeType.TaskPlanned)
							this.taskIdsActive[duplicateEvent.Task.InternalId]++;	//The counter will be decremented during TaskKilled
						duplicateEvent.subtype = SimulationTaskEvent.SubtypeType.TaskKilled;
						duplicateEvent.EventTime = this.simulationTime;
						ConsumeEvent(duplicateEvent);	//Recursive: kill the task immediatly instead of AddEvent() for performances reasons
						ConsumeEvent(simulationEvent);	//Recursive: try again the current TaskPlanned event next time
						return true;
					case TaskDuplicatesPolicy.KillNewDuplicates:
						simulationEvent.subtype = SimulationTaskEvent.SubtypeType.TaskKilled;
						simulationEvent.EventTime = this.simulationTime;
						this.taskIdsActive[currentTask.InternalId]++;
						ConsumeEvent(simulationEvent);	//Recursive: kill the task immediatly instead of AddEvent() for performances reasons
						if (currentTask.RelativeDate == RelativeDateType.Frequency)
							ConsumeFrequency(simulationEvent);
						return true;
					case TaskDuplicatesPolicy.KeepDuplicates:
					case TaskDuplicatesPolicy.Undefined:
					default:
						Debug.Assert(false, "This taskDuplicatesPolicy should not occur in ConsumeTaskDuplicate(): " + duplicateEvent.Task.DuplicatesPolicy.ToString());
						return false;
				}
			}
		}

		/// <summary>
		/// Sub-function of <see cref="ConsumeEvent"/>.
		/// </summary>
		/// <param name="simulationEvent">The simulation event processed,
		/// which type is <see cref="SimulationTaskEvent.SubtypeType.TaskPlanned"/>.</param>
		void ConsumeTaskPlanned(SimulationTaskEvent simulationEvent)
		{
			var currentTask = simulationEvent.Task;
			simulationEvent.subtype = SimulationTaskEvent.SubtypeType.TaskArrives;
			var originalEventTime = simulationEvent.EventTime;
			currentTask.SimulationTimeArrives = originalEventTime;
			if (currentTask.IsPhaseDependent)
				Debug.WriteLine("➨\t{0}\t(remaining: {1})", simulationEvent, currentTask.RemainingDuration);
			else
			{//Phase-independent task
				if (currentTask.PhaseTypes.Any(pt => this.simulationPhase.PhaseType.IsSubCodeOf(pt)))
				{
					if (currentTask.RelativeDate == RelativeDateType.Frequency)
						ConsumeFrequency(simulationEvent);
				}
				else
				{//This phase-independent task is not allowed in the current phase
					if ((currentTask.RelativeDate == RelativeDateType.Frequency) && (currentTask.DateOffset.Unit != TimeUnit.Undefined))
					{//Specific offset for the frequency: discard the current event and generate the next one.
						#if (DEBUG)
						simulationEvent.subtype = SimulationTaskEvent.SubtypeType.TaskCancelled;
						Debug.WriteLine("✘\t{0}\t(remaining: {1})", simulationEvent, currentTask.RemainingDuration);
						#endif
						simulationEvent.subtype = SimulationTaskEvent.SubtypeType.TaskPlanned;
						simulationEvent.EventTime = originalEventTime + currentTask.StartDate.NextValue(this.rand);
						Debug.WriteLine("➨\t{0}\t(remaining: {1})", simulationEvent, currentTask.RemainingDuration);
					}
					else
					{
						simulationEvent.subtype = SimulationTaskEvent.SubtypeType.TaskForNextPhase;
						Debug.WriteLine("◑\t{0}\t(remaining: {1})", simulationEvent, currentTask.RemainingDuration);
						simulationEvent.EventTime = SimulationTime.MaxValue;	//Task will be resumed when changing phase
					}
					AddEvent(simulationEvent);
					return;
				}
			}
			if (currentTask.SlaveTasks.Count > 0)
				foreach (var slaveTask in currentTask.SlaveTasks.Values.Where(t => t.Enabled))	//Start slaves
					EnqueueTaskArrival(this.simulationDataSet.TaskDictionaryExpanded.CreateTask(slaveTask, TaskLinkingType.Linked), simulationEvent.EventTime, original: true);
			this.taskIdsActive[currentTask.InternalId]++;	//Do after frequency case
			if ((currentTask.ParallelTasks.Count == 0) || ParallelReady(simulationEvent))
				ResumeTask(simulationEvent);
		}

		void ConsumeFrequency(SimulationTaskEvent simulationEvent)
		{
			var currentTask = simulationEvent.Task;
			var originalEventTime = simulationEvent.EventTime;
			var nextTask = this.simulationDataSet.TaskDictionaryExpanded.CreateTask(currentTask, TaskLinkingType.Linked);
			var nextTime = originalEventTime + nextTask.StartDate.NextValue(this.rand);
			if ((currentTask.RelativeTime != RelativeTimeType.AbsoluteStartTime) &&	//No need to process AbsoluteStartTime, as it is always planned properly
				(currentTask.DailyHourStart != currentTask.DailyHourEnd))
			{//There is a time window
				var offset = SimulationTime.DayTimeOffset(nextTask.DailyHourStart + nextTask.DateOffset.XValue, originalEventTime);	//DayTimeOffset is always positive
				if (offset.Positive && (offset < nextTask.StartDate.XValue))
				{//Correction if the task is not originally planned at the beginning of the time window.
					nextTime -= offset;	//Plan the next task properly
					var shift = SimulationTime.DayTimeOffset(currentTask.DailyHourStart, currentTask.DailyHourEnd) -
						currentTask.Duration.XValue;	//As much of the task as possible must be done after the current time
					if (shift.Positive && (shift < offset))
					{//Start virtually a bit before using a negative offset
						currentTask.SimulationTimeArrives -= offset - shift;
						currentTask.DiscardUntilNow(originalEventTime);
					}
				}
			}
			Debug.WriteLine("➨\t{0}\t(remaining: {1})", simulationEvent, currentTask.RemainingDuration);
			EnqueueTaskArrival(nextTask, nextTask.NextPossibleResume(nextTime), original: false);
		}

		/// <summary>
		/// Sub-function of <see cref="ConsumeEvent"/>.
		/// </summary>
		/// <param name="simulationEvent">The simulation event processed,
		/// which type is <see cref="SimulationTaskEvent.SubtypeType.TaskEnds"/> or
		/// <see cref="SimulationTaskEvent.SubtypeType.TaskKilled"/>.</param>
		void ConsumeTaskKilled(SimulationTaskEvent simulationEvent)
		{
			var currentTask = simulationEvent.Task;
			var originalEventTime = simulationEvent.EventTime;
			currentTask.ProcessUntilNow(originalEventTime, this.simulationPhase);
			Debug.WriteLine("☑\t{0}\t(remaining: {1})", simulationEvent, currentTask.RemainingDuration);
			this.taskIdsActive[currentTask.InternalId]--;
			Debug.Assert(this.taskIdsActive[currentTask.InternalId] >= 0, "The number of active tasks of a given type cannot be negative!");
			if (currentTask.PhaseInterruptionPolicy == PhaseInterruptionPolicies.Obligatory)
				this.nbObligatoryTasksActive--;
			if (currentTask.SlaveTasks.Count > 0)
				StopSlaves(simulationEvent);
			if (currentTask.ParallelTasks.Count > 0)
				StopParallels(simulationEvent);
			if (simulationEvent.subtype == SimulationTaskEvent.SubtypeType.TaskEnds)
			{
				if (currentTask.RemainingDuration.Positive)
					switch (currentTask.TaskInterruptionPolicy)
					{
						case TaskInterruptionPolicies.DropWithError:
						case TaskInterruptionPolicies.ContinueOrDropWithError:
							if (this.OnTaskError != null) this.OnTaskError(this.simulationPhase, currentTask);
							break;
						case TaskInterruptionPolicies.ContinueOrResumeWithError:
						case TaskInterruptionPolicies.ContinueOrResumeWithoutError:
							Debug.Assert(false, "A task must not have a remaining duration when it ends!");
							break;
					}
				switch (currentTask.RelativeDate)
				{//Next instance of the same task
					case RelativeDateType.AbsoluteStartMonthDay:
					case RelativeDateType.AbsoluteStartWeekDay:
					case RelativeDateType.RelativeStartFromPreviousStart:
						EnqueueTaskArrival(currentTask, originalEventTime, original: false);
						break;
				}
			}
		}

		string DebugQueuesPrint()
		{
			var result = new StringBuilder("\t\tASAP Queue:\n");
			foreach (var simulationEvent in this.asapQueue)
				result.Append("\t\t\t").AppendLine(simulationEvent.ToString());
			result.AppendLine("\t\tParallel Queue:");
			foreach (var simulationEvent in this.parallelQueue.Values)
				result.Append("\t\t\t").AppendLine(simulationEvent.ToString());
			result.AppendLine("\t\tEvent Queue:");
			foreach (var simulationEvent in this.eventQueue)
				result.Append("\t\t\t").AppendLine(simulationEvent.ToString());
			result.Length -= Environment.NewLine.Length;
			return result.ToString();
		}

		void ConsumeEvent(SimulationTaskEvent simulationEvent)
		{
			#if (VERBOSE_DEBUG)
			Debug.WriteLine("\t\t• Consume event: " + simulationEvent.ToString());
			Debug.WriteLine(DebugQueuesPrint());	//More debugging information
			#endif
			var originalEventTime = simulationEvent.EventTime;
			this.simulationTime = simulationEvent.EventTime;
			var callTaskDismiss = false;
			var currentTask = simulationEvent.Task;
			switch (simulationEvent.subtype)
			{
				case SimulationTaskEvent.SubtypeType.TaskPlanned:
					if ((currentTask.DuplicatesPolicy != TaskDuplicatesPolicy.KeepDuplicates) && (this.taskIdsActive[currentTask.InternalId] > 0) &&
						ConsumeTaskDuplicate(simulationEvent))
						return;
					ConsumeTaskPlanned(simulationEvent);
					break;
				case SimulationTaskEvent.SubtypeType.TaskAdjourned:
					currentTask.ProcessUntilNow(originalEventTime, this.simulationPhase);
					Debug.WriteLine("◧\t{0}\t(remaining: {1})", simulationEvent, currentTask.RemainingDuration);
					callTaskDismiss = true;
					simulationEvent.EventTime = currentTask.NextPossibleResume(originalEventTime, allowCurrentTime: false);
					simulationEvent.subtype = SimulationTaskEvent.SubtypeType.TaskResumes;
					AddEvent(simulationEvent);
					break;
				case SimulationTaskEvent.SubtypeType.TaskHibernated:
					currentTask.ProcessUntilNow(originalEventTime, this.simulationPhase);
					Debug.WriteLine("◑\t{0}\t(remaining: {1})", simulationEvent, currentTask.RemainingDuration);
					this.taskIdsActive[currentTask.InternalId]--;
					Debug.Assert(this.taskIdsActive[currentTask.InternalId] >= 0, "The number of active tasks of a given type cannot be negative!");
					callTaskDismiss = true;
					simulationEvent.EventTime = SimulationTime.MaxValue;	//Task will be resumed when changing phase
					simulationEvent.subtype = SimulationTaskEvent.SubtypeType.TaskAwakes;
					if (currentTask.SlaveTasks.Count > 0)
						StopSlaves(simulationEvent);
					AddEvent(simulationEvent);
					break;
				case SimulationTaskEvent.SubtypeType.TaskWorkInterrupted:
					currentTask.ProcessUntilNow(originalEventTime, this.simulationPhase);
					Debug.WriteLine("◭\t{0}\t(remaining: {1})", simulationEvent, currentTask.RemainingDuration);
					callTaskDismiss = true;
					simulationEvent.subtype = SimulationTaskEvent.SubtypeType.TaskWorkContinues;
					simulationEvent.EventTime = currentTask.NextPossibleResume(originalEventTime);	//Try immediatly to resume work
					AddEvent(simulationEvent);
					break;
				case SimulationTaskEvent.SubtypeType.TaskAwakes:
					this.taskIdsActive[currentTask.InternalId]++;
					if (currentTask.SlaveTasks.Count > 0)
						foreach (var slaveTask in currentTask.SlaveTasks.Values.Where(t => t.Enabled))	//Start slaves
							EnqueueTaskArrival(this.simulationDataSet.TaskDictionaryExpanded.CreateTask(slaveTask, TaskLinkingType.Linked), simulationEvent.EventTime, original: true);
					goto case SimulationTaskEvent.SubtypeType.TaskResumes;
				case SimulationTaskEvent.SubtypeType.TaskResumes:
				case SimulationTaskEvent.SubtypeType.TaskWorkContinues:
					Debug.WriteLine("➠\t{0}\t(remaining: {1})", simulationEvent, currentTask.RemainingDuration);
					ResumeTask(simulationEvent);
					break;
				case SimulationTaskEvent.SubtypeType.TaskEnds:
				case SimulationTaskEvent.SubtypeType.TaskKilled:
					callTaskDismiss = true;
					ConsumeTaskKilled(simulationEvent);
					break;
				default:
					Debug.WriteLine("✘\t Unknown event type: {0}\t(remaining: {1})", simulationEvent, currentTask.RemainingDuration);
					goto case SimulationTaskEvent.SubtypeType.TaskCancelled;
				case SimulationTaskEvent.SubtypeType.TaskCancelled:
					if (currentTask.PhaseInterruptionPolicy == PhaseInterruptionPolicies.Obligatory)
						this.nbObligatoryTasksActive--;
					break;
			}
			Debug.Assert(currentTask.RemainingDuration.PositiveOrZero, "A task cannot have a negative remaining duration!");
			if (callTaskDismiss && (currentTask.simulationCrewmenAssigned.Count >= currentTask.NumberOfCrewmenNeeded))
			{//The task was running and is stopped
				var crewmenFreed = currentTask.simulationCrewmenAssigned.ToList();
				this.dispatcher.TaskDismiss(this.simulationTime, this.simulationPhase, currentTask);
				RestaureAsap(crewmenFreed);
			}
		}

		void StopSlaves(SimulationTaskEvent simulationEvent, bool sort = true)
		{
			var task = simulationEvent.Task;
			//if (task.SlaveTasks.Count > 0)	//Should be tested before function call
			{
				var taskId = task.Id;
				var taskEventsUpdated = new List<SimulationTaskEvent>();
				#if (BINARY_HEAP)
				//if (sort) this.eventQueue.Sort();	//TODO: Speed: Try to remove
				#endif
				for (var i = this.eventQueue.Count - 1; i >= 0; i--)
				{//The events may be processed in a random order
					var myEvent = this.eventQueue[i];
					var myTaskMasters = myEvent.Task.MasterTasks;
					if (myTaskMasters.ContainsKey(taskId) &&	//Slave of the current task
						(!myTaskMasters.Values.Any(myTask => (myTask.Id != taskId) && (this.taskIdsActive[myTask.InternalId] > 0))))	//No other active master
					{
						myEvent.EventTime = this.simulationTime;
						if (myEvent.subtype == SimulationTaskEvent.SubtypeType.TaskPlanned)
							this.taskIdsActive[myEvent.Task.InternalId]++;	//The counter will be decreased during the processing of TaskKilled
						myEvent.subtype = SimulationTaskEvent.SubtypeType.TaskKilled;
						if (sort)
						{
							this.eventQueue.RemoveAt(i);	//Watch out for the index change!
							taskEventsUpdated.Add(myEvent);
						}
					}
				}
				if (sort)
					foreach (var myEvent in taskEventsUpdated)
						AddEvent(myEvent);
				for (var i = this.asapQueue.Count - 1; i >= 0; i--)	//Do the same for the ASAP queue
				{//The events may be processed in a random order
					var myEvent = this.asapQueue[i];
					var myTaskMasters = myEvent.Task.MasterTasks;
					if (myTaskMasters.ContainsKey(taskId) &&	//Slave of the current task
						(!myTaskMasters.Values.Any(myTask => (myTask.Id != taskId) && (this.taskIdsActive[myTask.InternalId] > 0))))	//No other active master
					{
						myEvent.EventTime = this.simulationTime;
						if (myEvent.subtype == SimulationTaskEvent.SubtypeType.TaskPlanned)
							this.taskIdsActive[myEvent.Task.InternalId]++;	//The counter will be decreased during the processing of TaskKilled
						myEvent.subtype = SimulationTaskEvent.SubtypeType.TaskKilled;
						this.asapQueue.RemoveAt(i);	//Watch out for the index change!
						AddEvent(myEvent);
					}
				}
			}
		}

		void StopParallels(SimulationTaskEvent simulationEvent)
		{
			var originalEventTime = simulationEvent.EventTime;
			foreach (var pt in simulationEvent.Task.ParallelTasks.Values)
			{
				var i = FindNextEventIndex(pt.Id);
				if (i >= 0)
				{
					var parallelEvent = this.eventQueue[i];
					if ((parallelEvent.subtype & SimulationTaskEvent.SubtypeType.TaskMetaNotStarted) != parallelEvent.subtype)
					{
						parallelEvent.Task.ProcessUntilNow(originalEventTime, this.simulationPhase);
						if (parallelEvent.Task.RemainingDuration.Positive)
						{
							this.eventQueue.RemoveAt(i);
							parallelEvent.subtype = SimulationTaskEvent.SubtypeType.TaskKilled;
							parallelEvent.EventTime = originalEventTime;
							ConsumeEvent(parallelEvent);	//Recursive: kill the task immediatly instead of AddEvent() for performances reasons
						}
					}
				}
			}
		}

		/// <summary>
		/// Sub-function of <see cref="EnqueueTaskArrival"/>.
		/// </summary>
		/// <param name="task">A task arriving</param>
		/// <param name="eventTime">Task arrival time</param>
		/// <param name="nextEventTime">Fist possible start time</param>
		/// <returns>A start time for the task</returns>
		static SimulationTime FindStartTime(SimulationTask task, SimulationTime eventTime, SimulationTime nextEventTime)
		{
		FindStartTime:
			switch (task.RelativeTime)
			{
				case RelativeTimeType.AbsoluteStartTime:
					{
						nextEventTime = nextEventTime.NextDayTime(task.DailyHourStart);
						break;
					}
				case RelativeTimeType.AbsoluteStopTime:
					{
						var workEnd = nextEventTime.NextDayTime(task.DailyHourEnd);
						if (workEnd <= eventTime) workEnd = nextEventTime.NextDayTime(task.DailyHourEnd, allowCurrentTime: false);
						nextEventTime = workEnd - task.Duration.XValue;	//TODO: Plan additional time?
						break;
					}
				case RelativeTimeType.TimeWindow:
				default:
					{
						var workStart = nextEventTime.NextDayTime(task.DailyHourStart);
						var workEnd = nextEventTime.NextDayTime(task.DailyHourEnd);
						if ((workStart < workEnd) || (nextEventTime >= workEnd))
							nextEventTime = workStart;	//TimeWindow, next day
						break;
					}
			}
			if ((!task.OnHolidays) && nextEventTime.IsSunday)
			{
				nextEventTime = nextEventTime.NextWeekTime(SimulationTime.OneDay, allowCurrentTime: false);
				goto FindStartTime;	//Quite legitimate use of goto, because an alternate do-while construct would be more complex and less efficient in this hot-path function
			}
			return nextEventTime;
		}

		void EnqueueTaskArrival(SimulationTask task, SimulationTime eventTime, bool original)
		{
			task.StartDate.NextValue(this.rand);
			task.Duration.NextValue(this.rand);
			task.PrepareForNextOccurrence();
			if (task.NumberOfCrewmenNeeded > task.simulationCurrentQualifications.Count)
			{//Invalid assignment
				this.taskIdsActive[task.InternalId]++;	//Counter will be decremented during TaskKilled
				AddEvent(new SimulationTaskEvent(task, eventTime, SimulationTaskEvent.SubtypeType.TaskKilled));
				if (this.OnTaskError != null) this.OnTaskError(this.simulationPhase, task);	//Event
				return;
			}
			var taskFromPreviousPhase = false;
			SimulationTime nextEventTime;
			switch (task.RelativeDate)
			{
				case RelativeDateType.AbsoluteStartMonthDay:
					nextEventTime = eventTime.NextMonthTime(task.StartDate.XValue) + task.DateOffset.XValue;
					break;
				case RelativeDateType.AbsoluteStartWeekDay:
					nextEventTime = eventTime.NextWeekTime(task.StartDate.XValue) + task.DateOffset.XValue;
					break;
				case RelativeDateType.Frequency:
					Debug.Assert(!this.eventQueue.Any(ste => (ste.Task.Id == task.Id) && (ste.subtype == SimulationTaskEvent.SubtypeType.TaskPlanned)), "A frequency task must not be planned from a previous phase?");
					nextEventTime = original ? eventTime + task.DateOffset.XValue : eventTime;
					break;
				case RelativeDateType.RelativeStartFromPreviousStart:
					if (task.SimulationTimeArrives.Negative)
					{//TODO: kill possible duplicates if duplicates are forbidden in the new phase
						var previousTaskEvent = NextEventOrDefault(task.Id);
						if (previousTaskEvent != null)
						{//Kill previously planned (from previous phase) tasks and use them as a reference
							Debug.WriteLine("■\tReplace {0}", previousTaskEvent);
							this.dispatcher.TaskDismiss(eventTime, this.simulationPhase, previousTaskEvent.Task);
							if (previousTaskEvent.subtype == SimulationTaskEvent.SubtypeType.TaskPlanned)	//TODO: Improve detection of whether the task was running or not
								task.SimulationTimeArrives = previousTaskEvent.Task.SimulationTimeArrives - previousTaskEvent.Task.StartDate.XValue;
							else
							{//The task was already running
								taskFromPreviousPhase = true;
								previousTaskEvent.Task.ProcessUntilNow(this.simulationTime, this.simulationPhase);
								task.SimulationTimeArrives = previousTaskEvent.Task.SimulationTimeArrives;
								task.RemainingDuration = previousTaskEvent.Task.RemainingDuration;
								task.SleepUntilNow(this.simulationTime);	//To set a new reference time
							}
							previousTaskEvent.subtype = SimulationTaskEvent.SubtypeType.TaskCancelled;
							previousTaskEvent.EventTime = this.simulationTime;
						}
					}
					if (task.SimulationTimeArrives.Negative)
					{//For the first occurrence (initialiation)
						nextEventTime = eventTime - task.StartDate.XValue;
						var attempts = 8;
						while ((nextEventTime < eventTime) && (--attempts > 0))
						{
							task.StartDate.NextValue(this.rand);
							nextEventTime += task.StartDate.XValue;
						}
					}
					else if (taskFromPreviousPhase) nextEventTime = this.simulationTime;
					else nextEventTime = task.SimulationTimeArrives + task.StartDate.XValue;
					break;
				case RelativeDateType.RelativeStartFromStartOfPhase:
					nextEventTime = this.simulationPhase.simulationTimeBegin + task.StartDate.XValue;
					break;
				case RelativeDateType.RelativeStartFromEndOfPhase:
					nextEventTime = this.simulationPhase.simulationTimeBegin + this.simulationPhase.Duration.XValue - task.StartDate.XValue;	//TODO: Plan additional time?
					break;
				case RelativeDateType.RelativeStopFromEndOfPhase:
					nextEventTime = this.simulationPhase.simulationTimeBegin;	//TODO: Report error properly when task is not performed on time
					break;
				case RelativeDateType.TriggeredByAnEvent:
				default:
					nextEventTime = eventTime + task.DateOffset.XValue;
					break;
			}
			if (nextEventTime < eventTime) nextEventTime = eventTime;

			nextEventTime = FindStartTime(task, eventTime, nextEventTime);

			if (task.PhaseInterruptionPolicy == PhaseInterruptionPolicies.Obligatory) this.nbObligatoryTasksActive++;
			if (taskFromPreviousPhase)	//A task was already running from a previous phase
				AddEvent(new SimulationTaskEvent(task, nextEventTime, SimulationTaskEvent.SubtypeType.TaskResumes));
			else
			{
				var simulationEvent = new SimulationTaskEvent(task, nextEventTime, SimulationTaskEvent.SubtypeType.TaskPlanned);
				Debug.WriteLine("{0}\t{1}\t(duration: {2})",
					simulationEvent.Task.PhaseInterruptionPolicy == PhaseInterruptionPolicies.Obligatory ? '❍' : '☐',
					simulationEvent, task.Duration.XValue);
				AddEvent(simulationEvent);
			}
		}

		void InterruptTask(SimulationTaskEvent simulationEvent, bool tryAssignmentAgainNow, bool recursive)
		{//TODO: Interruption errors
			var task = simulationEvent.Task;
			task.ProcessUntilNow(this.simulationTime, this.simulationPhase);
			if ((task.TaskInterruptionPolicy == TaskInterruptionPolicies.DropWithError) || (task.TaskInterruptionPolicy == TaskInterruptionPolicies.DropWithoutError) ||
				((!tryAssignmentAgainNow) && ((task.TaskInterruptionPolicy == TaskInterruptionPolicies.ContinueOrDropWithError) || (task.TaskInterruptionPolicy == TaskInterruptionPolicies.ContinueOrDropWithoutError))))
				simulationEvent.subtype = SimulationTaskEvent.SubtypeType.TaskKilled;	//The task cannot be continued
			else
			{//Continue or resume
				#if (DEBUG)
				simulationEvent.subtype = SimulationTaskEvent.SubtypeType.TaskWorkInterrupted;
				simulationEvent.EventTime = this.simulationTime;
				#endif
				Debug.WriteLine("◭\t{0}\t(remaining: {1})", simulationEvent, task.RemainingDuration);
				if ((task.TaskInterruptionPolicy == TaskInterruptionPolicies.ContinueOrResumeWithError) && (this.OnTaskError != null))
					this.OnTaskError(this.simulationPhase, task);
				simulationEvent.subtype = SimulationTaskEvent.SubtypeType.TaskWorkContinues;
				simulationEvent.EventTime = task.NextPossibleResume(this.simulationTime);
			}
			this.dispatcher.TaskDismiss(this.simulationTime, this.simulationPhase, task);
			if (recursive && task.ParallelTasks.Count > 0)
				InterruptTaskIds(task.ParallelTasks.Keys, tryAssignmentAgainNow: false, recursive: false);
			if (simulationEvent.subtype == SimulationTaskEvent.SubtypeType.TaskWorkContinues) AddAsap(simulationEvent);
			else AddEvent(simulationEvent);
		}

		public delegate void TasksInterruptedCallback(IEnumerable<SimulationTask> tasks, bool tryAssignmentAgainNow = false, bool recursive = true);

		void InterruptTasks(IEnumerable<SimulationTask> tasks, bool tryAssignmentAgainNow = false, bool recursive = true)
		{
			foreach (var task in tasks)
			{
				var i = FindNextEventIndex(task);	//Compare references
				if (i >= 0)
				{
					var simulationEvent = this.eventQueue[i];
					this.eventQueue.RemoveAt(i);
					InterruptTask(simulationEvent, tryAssignmentAgainNow, recursive);
					//Interrupt only the first instance of a given task, assuming that this is the one that is active (to be refined).
				}
			}
		}

		/// <summary>
		/// Used to interrupt tasks when we do not know a concrete instance of the tasks in the simulation.
		/// </summary>
		void InterruptTaskIds(IEnumerable<int> taskIds, bool tryAssignmentAgainNow = false, bool recursive = true)
		{
			foreach (var taskId in taskIds)
			{//TODO: Find a better way to know what tasks to interrupt
				var i = FindNextEventIndex(ste => (ste.Task.Id == taskId) && ((ste.subtype & SimulationTaskEvent.SubtypeType.TaskMetaNotStarted) != ste.subtype));
				if (i >= 0)
				{
					var simulationEvent = this.eventQueue[i];
					this.eventQueue.RemoveAt(i);
					InterruptTask(simulationEvent, tryAssignmentAgainNow, recursive);
					//Interrupt only the first instance of a given task, assuming that this is the one that is active (to be refined).
				}
			}
		}

		void ResumeTask(SimulationTaskEvent simulationEvent)
		{
			var task = simulationEvent.Task;
			Debug.Assert((task.TaskType == (int)StandardTaskType.InternalWait) || task.Allowed(this.simulationPhase), "A task must not be resumed in phases where it is not allowed!");
			task.SleepUntilNow(simulationEvent.EventTime);
			var taskAssignmentTimeExpire = this.dispatcher.TaskAssignment(simulationEvent.EventTime, this.simulationPhase, task, this.InterruptTasks);
			if (taskAssignmentTimeExpire.Negative) InterruptTask(simulationEvent, tryAssignmentAgainNow: false, recursive: true);	//Task was not assigned
			else
			{
				var remainingProcessingTime = task.RemainingProcessingTime();
				var taskNextEventTime = simulationEvent.EventTime + remainingProcessingTime;
				var interruptionTime = taskAssignmentTimeExpire;
				simulationEvent.subtype = SimulationTaskEvent.SubtypeType.TaskWorkInterrupted;
				if (task.Rotation.Positive && (remainingProcessingTime > task.Rotation))
				{//Rotation	//TODO: Set to +infinity to disable rotation instead of zero, for optimisation
					var nextInterruption = simulationEvent.EventTime + task.Rotation;
					if (interruptionTime > nextInterruption) interruptionTime = nextInterruption;
				}
				if (task.DailyHourStart != task.DailyHourEnd)
				{//TimeWindow end
					var nextInterruption = simulationEvent.EventTime.NextDayTime(task.DailyHourEnd, allowCurrentTime: false);
					if (interruptionTime > nextInterruption)
					{
						interruptionTime = nextInterruption;
						simulationEvent.subtype = SimulationTaskEvent.SubtypeType.TaskAdjourned;
					}
				}
				if (!task.OnHolidays)
				{//Sunday start
					var nextInterruption = simulationEvent.EventTime.NextWeekTime(SimulationTime.OneWeek);
					if (interruptionTime > nextInterruption)
					{
						interruptionTime = nextInterruption;
						simulationEvent.subtype = SimulationTaskEvent.SubtypeType.TaskAdjourned;
					}
				}
				if (interruptionTime >= taskNextEventTime)
				{//No planned interruption
					simulationEvent.subtype = SimulationTaskEvent.SubtypeType.TaskEnds;
					simulationEvent.EventTime = taskNextEventTime;
				}
				else
				{//The task does not have time to complete in one row	//TODO: Refactor and add rationale
					if ((task.PhaseInterruptionPolicy == PhaseInterruptionPolicies.DoNotInterrupt) &&
						(simulationEvent.subtype == SimulationTaskEvent.SubtypeType.TaskAdjourned))
					{
						simulationEvent.subtype = SimulationTaskEvent.SubtypeType.TaskEnds;	//This is in order not to have remaining hours if the task was started somewhere in the middle of the time window
						switch (task.RelativeDate)
						{
							case RelativeDateType.RelativeStartFromPreviousStart:
								var timeOffset = task.SimulationTimeArrives.DayTime - task.DailyHourStart;
								if (timeOffset.Positive)	//Fictive negative offset, so that later relative occurrences will start at the expected time
									task.SimulationTimeArrives -= timeOffset;
								break;
						}
					}
					simulationEvent.EventTime = interruptionTime;
				}
				AddEvent(simulationEvent);
			}
		}

		#region Priority queue
		/// <remarks>
		/// Tests with SortedSet have shown that SortedSet would be much slower for the current case.
		/// </remarks>
		#if (BINARY_HEAP)
		readonly BinaryHeap<SimulationTaskEvent> eventQueue = new BinaryHeap<SimulationTaskEvent>(128);
		#else
		readonly List<SimulationTaskEvent> eventQueue = new List<SimulationTaskEvent>();
		#endif

		/// <summary>
		/// Give a possibility to sort in reverse order when set to -1, otherwise natural order when set to 1.
		/// </summary>
		#if (REVERSE_QUEUE)
		internal const int EventOrderSign = -1;
		#else
		internal const int EventOrderSign = 1;
		#endif

		SimulationTaskEvent NextEventOrDefault(int taskId)
		{
			#if (REVERSE_QUEUE)
			return this.eventQueue.LastOrDefault(ste => ste.Task.Id == taskId);
			#else
			return this.eventQueue.FirstOrDefault(ste => ste.Task.Id == taskId);
			#endif
		}

		int FindNextEventIndex(Predicate<SimulationTaskEvent> match)
		{
			#if (REVERSE_QUEUE)
			return this.eventQueue.FindLastIndex(match);
			#else
			return this.eventQueue.FindIndex(match);
			#endif
		}

		int FindNextEventIndex(int taskId)
		{
			#if (REVERSE_QUEUE)
			return this.eventQueue.FindLastIndex(se => se.Task.Id == taskId);
			#else
			return this.eventQueue.FindIndex(se => se.Task.Id == taskId);
			#endif
		}

		int FindNextEventIndex(SimulationTask task)
		{
			#if (REVERSE_QUEUE)
			return this.eventQueue.FindLastIndex(se => se.Task == task);	//Compare references
			#else
			return this.eventQueue.FindIndex(se => se.Task == task);	//Compare references
			#endif
		}

		SimulationTaskEvent PeekEvent()
		{
			#if (REVERSE_QUEUE)
			return this.eventQueue[this.eventQueue.Count - 1];
			#else
			return this.eventQueue[0];
			#endif
		}

		SimulationTaskEvent RemoveEvent()
		{
			#if (BINARY_HEAP)
			return this.eventQueue.Remove();
			#elif (REVERSE_QUEUE)
			var simulationEvent = this.eventQueue[this.eventQueue.Count - 1];
			this.eventQueue.RemoveAt(this.eventQueue.Count - 1);	//O(1) so better performance than RemoveAt(0)
			return simulationEvent;
			#else
			var simulationEvent = this.eventQueue[0];
			this.eventQueue.RemoveAt(0);	//Hot path O(n)	http://msdn.microsoft.com/en-us/library/5cw9x18z.aspx
			return simulationEvent;
			#endif
		}

		void AddEvent(SimulationTaskEvent simulationEvent)
		{
			Debug.Assert(((simulationEvent.subtype & SimulationTaskEvent.SubtypeType.TaskMetaStart) != simulationEvent.subtype) ||
				(simulationEvent.EventTime >= SimulationTime.MaxValue) || simulationEvent.Task.IsAllowedTime(simulationEvent.EventTime),
				"Task assignment must be within allowed daily hours!");
			this.nbEventInsertions++;	//Count number of insertions to get some metrics about the performance of the logic
			#if (BINARY_HEAP)
			this.eventQueue.Add(simulationEvent);
			#else
			#if (REVERSE_QUEUE)
			var index = this.eventQueue.BinarySearch(simulationEvent);
			#else
			var index = this.eventQueue.BinarySearch(simulationEvent);
			#endif
			if (index < 0) this.eventQueue.Insert(~index, simulationEvent);
			else
			{
				this.eventQueue.Insert(index, simulationEvent);
				Debug.WriteLine("❢\tWarning! Two identical events in queue: {0}", simulationEvent);
			}
			#endif
		}
		#endregion

		#region ASAP queue
		/// <summary>
		/// List of events to be processed as soon as possible (ASAP), such as interrupted tasks.
		/// </summary>
		readonly List<SimulationTaskEvent> asapQueue = new List<SimulationTaskEvent>();

		void AddAsap(SimulationTaskEvent simulationEvent)
		{
			var index = this.asapQueue.BinarySearch(simulationEvent, SimulationEventAsapComparer.Instance);
			if (index < 0) this.asapQueue.Insert(~index, simulationEvent);
			else
			{
				this.asapQueue.Insert(index, simulationEvent);
				Debug.WriteLine("❢\tWarning! Two identical events in ASAP queue: {0}", simulationEvent);
			}
		}

		/// <summary>
		/// Re-activate all relevant tasks that are awaiting a resource that has just been freed.
		/// </summary>
		/// <param name="crewmenFreed"></param>
		void RestaureAsap(List<Crewman> crewmenFreed)
		{
			var allRelevant = crewmenFreed.Count <= 0;	//No optimisation when a task stops that was not using any resource
			for (var i = this.asapQueue.Count - 1; i >= 0; i--)
			{
				var simulationEvent = this.asapQueue[i];
				var task = simulationEvent.Task;
				if (allRelevant || (task.NumberOfCrewmenNeeded > 1) || (task.ParallelTasks.Count > 0))
				{//No optimisation for parallel tasks
					this.asapQueue.RemoveAt(i);
					simulationEvent.EventTime = task.NextPossibleResume(this.simulationTime);
					AddEvent(simulationEvent);
				}
				else
					for (var j = crewmenFreed.Count - 1; j >= 0; j--)
					{
						var crewmanFreed = crewmenFreed[j];
						if (task.simulationCurrentQualifications.ContainsKey(crewmanFreed))
						{
							this.asapQueue.RemoveAt(i);
							simulationEvent.EventTime = task.NextPossibleResume(this.simulationTime);
							if (simulationEvent.EventTime <= this.simulationTime)
								crewmenFreed.RemoveAt(j);	//Remove the resource only if the found task can start immediatly
							AddEvent(simulationEvent);
							break;
						}
					}
			}
		}

		int FindNextAsapIndex(int taskId)
		{
			return this.asapQueue.FindLastIndex(se => se.Task.Id == taskId);	//Stored in reverse order
		}
		#endregion

		#region Parallel tasks queue
		readonly Dictionary<int, SimulationTaskEvent> parallelQueue = new Dictionary<int, SimulationTaskEvent>();

		/// <summary>
		/// Check if all the needed parallel tasks have arrived, and if yes, resume the waiting parallel tasks,
		/// and if not, put aside the new parallel task.
		/// </summary>
		/// <param name="simulationEvent">The event to process</param>
		/// <returns>true if all needed parallel tasks have arrived, false otherwise.</returns>
		bool ParallelReady(SimulationTaskEvent simulationEvent)
		{
			if (simulationEvent.subtype != SimulationTaskEvent.SubtypeType.TaskArrives)
				return true;	//The parallel tasks were already started
			var task = simulationEvent.Task;
			var myParallelTaskEvents = new List<SimulationTaskEvent>();
			foreach (var parallelTaskId in task.ParallelTasks.Keys)
			{
				SimulationTaskEvent mySimulationEvent;
				if (this.parallelQueue.TryGetValue(parallelTaskId, out mySimulationEvent))
					myParallelTaskEvents.Add(mySimulationEvent);
				else
				{
					myParallelTaskEvents.Clear();
					simulationEvent.subtype = SimulationTaskEvent.SubtypeType.TaskWaitingParallel;
					this.parallelQueue.Add(task.Id, simulationEvent);	//Parallel tasks cannot have duplicates
					Debug.WriteLine("◭\t{0}\t(remaining: {1})", simulationEvent, task.RemainingDuration);
					return false;
				}
			}
			foreach (var myParallelTaskEvent in myParallelTaskEvents)
			{
				this.parallelQueue.Remove(myParallelTaskEvent.Task.Id);	//Parallel tasks cannot have duplicates
				myParallelTaskEvent.subtype = SimulationTaskEvent.SubtypeType.TaskWorkContinues;	//TODO: Consider using a custom type of status for that
				myParallelTaskEvent.EventTime = simulationEvent.EventTime;
				AddEvent(myParallelTaskEvent);
			}
			myParallelTaskEvents.Clear();
			return true;
		}
		#endregion
	}
}
