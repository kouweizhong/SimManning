﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace SimManning.IO
{
	public static class XmlIO
	{
		public static string XmlDomain = String.Empty;
		public static string XmlDomainVersion = "1.1";

		internal static readonly XmlReaderSettings SimManningXmlReaderSettings = new XmlReaderSettings
		{
			CloseInput = true,
			ConformanceLevel = ConformanceLevel.Document,
			IgnoreComments = true,
			IgnoreProcessingInstructions = true,
			IgnoreWhitespace = true
		};

		internal static readonly XmlWriterSettings SimManningXmlWriterSettings = new XmlWriterSettings
		{
			CloseOutput = true,
			Indent = true,
			IndentChars = "\t",
			OmitXmlDeclaration = true
		};

		/// <summary>
		/// Load a list of type codes.
		/// </summary>
		/// <param name="typesPath">Path to the XML file containing the types declaration</param>
		/// <param name="typeName">Type to be loaded</param>
		/// <returns>A collection of code=>description (e.g. 31=>"Cargo handling")</returns>
		public static Dictionary<int, string> LoadTypesList(string typesPath, string typeName)
		{//TODO: Cache the result in the Task and Phase objects?
			var dictionary = new Dictionary<int, string>();
			var xmlTypes = XDocument.Load(typesPath);	//TODO: Catch errors
			var myTypes = from types in xmlTypes.Root.Elements("Types")
						  where types.Attribute("id").Value == typeName
						  select types;
			var typeList = from myType in myTypes.Descendants("Type")	//Can be written in one expression?
						   select myType;
			foreach (var myType in typeList)
				dictionary.Add(myType.Attribute("code").Value.ParseInteger(), myType.Attribute("name").Value);
			return dictionary;
		}

		#region Parse extensions
		public static TimeUnit ParseTimeUnit(this XAttribute attribute)
		{
			return (attribute == null ? String.Empty : attribute.Value).ParseTimeUnit();
		}

		/*public static TimeDistribution ParseTimeDistribution(TimeUnit timeUnit, XAttribute min, XAttribute mode, XAttribute max)
		{
			return new TimeDistribution(timeUnit, min == null ? String.Empty : min.Value, mode == null ? String.Empty : mode.Value, max == null ? String.Empty : max.Value);
		}*/

		public static TimeDistribution ParseTimeDistribution(XAttribute timeUnit, XAttribute min, XAttribute mode, XAttribute max)
		{
			return new TimeDistribution(timeUnit.ParseTimeUnit(), min == null ? String.Empty : min.Value, mode == null ? String.Empty : mode.Value, max == null ? String.Empty : max.Value);
		}

		public static bool ParseBoolean(this XAttribute attribute)
		{
			return (attribute == null ? String.Empty : attribute.Value).ParseBoolean();
		}

		public static int ParseInteger(this XAttribute attribute)
		{
			return (attribute == null ? String.Empty : attribute.Value).ParseInteger();
		}

		/*static double ParseReal(this XAttribute xAttribute)
		{
			return (xAttribute == null ? String.Empty : xAttribute.Value).ParseReal();
		}*/

		public static SimulationTime ParseSimulationTime(this XAttribute value, TimeUnit timeUnit)
		{
			return new SimulationTime(timeUnit, value == null ? String.Empty : value.Value);
		}

		public static SimulationTask.TaskInterruptionPolicies ParseTaskInterruptionPolicy(this XAttribute attribute)
		{
			return SimulationTask.ParseTaskInterruptionPolicy(attribute == null ? String.Empty : attribute.Value);
		}

		public static SimulationTask.PhaseInterruptionPolicies ParsePhaseInterruptionPolicy(this XAttribute attribute)
		{
			return SimulationTask.ParsePhaseInterruptionPolicy(attribute == null ? String.Empty : attribute.Value);
		}

		public static SimulationTask.ScenarioInterruptionPolicies ParseScenarioInterruptionPolicy(this XAttribute attribute)
		{
			return SimulationTask.ParseScenarioInterruptionPolicy(attribute == null ? String.Empty : attribute.Value);
		}

		public static SimulationTask.RelativeTimeType ParseRelativeTimeType(this XAttribute attribute)
		{
			return SimulationTask.ParseRelativeTimeType(attribute == null ? String.Empty : attribute.Value);
		}
		#endregion
	}
}