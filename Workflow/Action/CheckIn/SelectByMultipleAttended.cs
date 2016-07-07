﻿// <copyright>
// Copyright 2013 by the Spark Development Network
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>
//
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using Rock;
using Rock.Attribute;
using Rock.CheckIn;
using Rock.Data;
using Rock.Model;
using Rock.Web.Cache;
using Rock.Workflow;
using Rock.Workflow.Action.CheckIn;

namespace cc.newspring.AttendedCheckIn.Workflow.Action.CheckIn
{
    /// <summary>
    /// Calculates and updates the LastCheckIn property on check-in objects
    /// </summary>
    [Description( "Select multiple services from person's previous attendance" )]
    [Export( typeof( ActionComponent ) )]
    [ExportMetadata( "ComponentName", "Select By Multiple Services Attended" )]
    [GroupTypesField( "Room Balance Grouptypes", "Select the grouptype(s) you want to room balance. This will auto-assign the group or location (within a grouptype) with the least number of people.", false, order: 0 )]
    [IntegerField( "Balancing Override", "Enter the maximum difference between two locations before room balancing starts to override previous attendance.  The default value is 5.", false, 5, order: 1 )]
    [TextField( "Excluded Locations", "Enter a comma-delimited list of location name(s) to manually exclude from room balancing (like catch-all rooms).", false, "Base Camp", order: 2 )]
    [IntegerField( "Max Assignments", "Enter the maximum number of auto-assignments based on previous attendance.  The default value is 5.", false, 5, order: 3 )]
    [AttributeField( Rock.SystemGuid.EntityType.PERSON, "Person Special Needs Attribute", "Select the attribute used to filter special needs people.", false, false, "8B562561-2F59-4F5F-B7DC-92B2BB7BB7CF", order: 4 )]
    public class SelectByMultipleAttended : CheckInActionComponent
    {
        /// <summary>
        /// Executes the specified workflow.
        /// </summary>
        /// <param name="rockContext">The rock context.</param>
        /// <param name="action">The workflow action.</param>
        /// <param name="entity">The entity.</param>
        /// <param name="errorMessages">The error messages.</param>
        /// <returns></returns>
        /// <exception cref="System.NotImplementedException"></exception>
        public override bool Execute( RockContext rockContext, Rock.Model.WorkflowAction action, Object entity, out List<string> errorMessages )
        {
            var checkInState = GetCheckInState( entity, out errorMessages );
            if ( checkInState == null )
            {
                return false;
            }

            int selectFromDaysBack = checkInState.CheckInType.AutoSelectDaysBack;
            var roomBalanceGroupTypes = GetAttributeValue( action, "RoomBalanceGrouptypes" ).SplitDelimitedValues().AsGuidList();
            int roomBalanceOverride = GetAttributeValue( action, "BalancingOverride" ).AsIntegerOrNull() ?? 5;
            int maxAssignments = GetAttributeValue( action, "MaxAssignments" ).AsIntegerOrNull() ?? 5;
            var excludedLocations = GetAttributeValue( action, "ExcludedLocations" ).SplitDelimitedValues( whitespace: false )
                .Select( s => s.Trim() );

            // get the admin-selected attribute key instead of using a hardcoded key
            var personSpecialNeedsKey = string.Empty;
            var personSpecialNeedsGuid = GetAttributeValue( action, "PersonSpecialNeedsAttribute" ).AsGuid();
            if ( personSpecialNeedsGuid != Guid.Empty )
            {
                personSpecialNeedsKey = AttributeCache.Read( personSpecialNeedsGuid, rockContext ).Key;
            }

            // log a warning if the attribute is missing or invalid
            if ( string.IsNullOrWhiteSpace( personSpecialNeedsKey ) )
            {
                action.AddLogEntry( string.Format( "The Person Special Needs attribute is not selected or invalid for '{0}'.", action.ActionType.Name ) );
            }

            var family = checkInState.CheckIn.Families.FirstOrDefault( f => f.Selected );
            if ( family != null )
            {
                var cutoffDate = RockDateTime.Today.AddDays( selectFromDaysBack * -1 );
                var attendanceService = new AttendanceService( rockContext );

                // only process people who have been here before
                foreach ( var previousAttender in family.People.Where( p => p.Selected && !p.FirstTime ) )
                {
                    // get a list of this person's available grouptypes
                    var availableGroupTypeIds = previousAttender.GroupTypes.Select( gt => gt.GroupType.Id ).ToList();

                    var lastDateAttendances = attendanceService.Queryable().Where( a =>
                            a.PersonAlias.PersonId == previousAttender.Person.Id &&
                            availableGroupTypeIds.Contains( a.Group.GroupTypeId ) &&
                            a.StartDateTime >= cutoffDate && a.DidAttend == true )
                        .OrderByDescending( a => a.StartDateTime ).Take( maxAssignments )
                        .ToList();

                    if ( lastDateAttendances.Any() )
                    {
                        var lastAttended = lastDateAttendances.Max( a => a.StartDateTime ).Date;
                        var numAttendances = lastDateAttendances.Count( a => a.StartDateTime >= lastAttended );
                        foreach ( var groupAttendance in lastDateAttendances.Where( a => a.StartDateTime >= lastAttended ) )
                        {
                            bool currentlyCheckedIn = false;
                            var serviceCutoff = groupAttendance.StartDateTime;
                            if ( serviceCutoff > RockDateTime.Now.Date )
                            {
                                // calculate the service window to determine if people are still checked in
                                var serviceTime = groupAttendance.StartDateTime.Date + groupAttendance.Schedule.NextStartDateTime.Value.TimeOfDay;
                                var serviceStart = serviceTime.AddMinutes( ( groupAttendance.Schedule.CheckInStartOffsetMinutes ?? 0 ) * -1.0 );
                                serviceCutoff = serviceTime.AddMinutes( ( groupAttendance.Schedule.CheckInEndOffsetMinutes ?? 0 ) );
                                currentlyCheckedIn = RockDateTime.Now > serviceStart && RockDateTime.Now < serviceCutoff;
                            }

                            // override exists in case they are currently checked in or have special needs
                            bool useCheckinOverride = currentlyCheckedIn || previousAttender.Person.GetAttributeValue( personSpecialNeedsKey ).AsBoolean();

                            // get a list of room balanced grouptype ID's since CheckInGroup model is a shallow clone
                            var roomBalanceGroupTypeIds = previousAttender.GroupTypes.Where( gt => roomBalanceGroupTypes.Contains( gt.GroupType.Guid ) )
                                .Select( gt => gt.GroupType.Id ).ToList();

                            // Start with filtered groups unless they have abnormal age and grade parameters (1%)
                            var groupType = previousAttender.GroupTypes.FirstOrDefault( gt => gt.GroupType.Id == groupAttendance.Group.GroupTypeId && ( !gt.ExcludedByFilter || useCheckinOverride ) );
                            if ( groupType != null )
                            {
                                CheckInGroup group = null;
                                if ( groupType.Groups.Count == 1 )
                                {
                                    // Only a single group is open
                                    group = groupType.Groups.FirstOrDefault( g => !g.ExcludedByFilter || useCheckinOverride );
                                }
                                else
                                {
                                    // Pick the group they last attended, as long as it's open or what they're currently checked into
                                    group = groupType.Groups.FirstOrDefault( g => g.Group.Id == groupAttendance.GroupId && ( !g.ExcludedByFilter || useCheckinOverride ) );

                                    // room balance only on new check-ins
                                    if ( group != null && roomBalanceGroupTypeIds.Contains( group.Group.GroupTypeId ) && !useCheckinOverride )
                                    {
                                        //TODO: use KioskLocationAttendance and group.AvailableForSchedule to room balance by service time attendance, not the entire day
                                        var currentAttendance = group.Locations.Select( l => KioskLocationAttendance.Read( l.Location.Id ).CurrentCount ).Sum();
                                        var lowestAttendedGroup = groupType.Groups.Where( g => !g.ExcludedByFilter && !excludedLocations.Contains( g.Group.Name ) )
                                            .Select( g => new { Group = g, Attendance = g.Locations.Select( l => KioskLocationAttendance.Read( l.Location.Id ).CurrentCount ).Sum() } )
                                            .OrderBy( g => g.Attendance )
                                            .FirstOrDefault();

                                        if ( lowestAttendedGroup != null && lowestAttendedGroup.Attendance < ( currentAttendance - roomBalanceOverride + 1 ) )
                                        {
                                            group = lowestAttendedGroup.Group;
                                        }
                                    }
                                }

                                if ( group != null )
                                {
                                    CheckInLocation location = null;
                                    if ( group.Locations.Count == 1 )
                                    {
                                        // Only a single location is open
                                        location = group.Locations.FirstOrDefault( l => !l.ExcludedByFilter || useCheckinOverride );
                                    }
                                    else
                                    {
                                        // Pick the location they last attended, as long as it's open or what they're currently checked into
                                        location = group.Locations.FirstOrDefault( l => l.Location.Id == groupAttendance.LocationId && ( !l.ExcludedByFilter || useCheckinOverride ) );

                                        // room balance only on new check-ins
                                        if ( location != null && roomBalanceGroupTypeIds.Contains( group.Group.GroupTypeId ) && !useCheckinOverride )
                                        {
                                            //TODO: use KioskLocationAttendance and location.AvailableForSchedule to room balance by service time attendance, not the entire day
                                            var currentAttendance = KioskLocationAttendance.Read( location.Location.Id ).CurrentCount;
                                            var lowestAttendedLocation = group.Locations.Where( l => !l.ExcludedByFilter && !excludedLocations.Contains( l.Location.Name ) )
                                                .Select( l => new { Location = l, Attendance = KioskLocationAttendance.Read( l.Location.Id ).CurrentCount } )
                                                .OrderBy( l => l.Attendance )
                                                .FirstOrDefault();

                                            if ( lowestAttendedLocation != null && lowestAttendedLocation.Attendance < ( currentAttendance - roomBalanceOverride + 1 ) )
                                            {
                                                location = lowestAttendedLocation.Location;
                                            }
                                        }
                                    }

                                    if ( location != null )
                                    {
                                        CheckInSchedule schedule = null;
                                        if ( location.Schedules.Count == 1 )
                                        {
                                            schedule = location.Schedules.FirstOrDefault( s => !s.ExcludedByFilter || useCheckinOverride );
                                        }
                                        else
                                        {
                                            // if assigning to multiple services or currently checked in (not SN, otherwise they would get the wrong auto-schedule)
                                            if ( numAttendances > 1 || currentlyCheckedIn )
                                            {
                                                // pick what they last attended last
                                                schedule = location.Schedules.FirstOrDefault( s => s.Schedule.Id == groupAttendance.ScheduleId && ( !s.ExcludedByFilter || useCheckinOverride ) );
                                            }

                                            // otherwise pick the earliest available schedule
                                            schedule = schedule ?? location.Schedules.OrderBy( s => s.Schedule.StartTimeOfDay ).FirstOrDefault( s => !s.ExcludedByFilter );
                                        }

                                        if ( schedule != null )
                                        {
                                            // it's impossible to currently be checked in unless these match exactly
                                            if ( group.Group.Id == groupAttendance.GroupId && location.Location.Id == groupAttendance.LocationId && schedule.Schedule.Id == groupAttendance.ScheduleId )
                                            {
                                                // Checkout would've removed the attendance or set the EndDateTime
                                                var endOfCheckinWindow = groupAttendance.EndDateTime ?? serviceCutoff;
                                                schedule.LastCheckIn = endOfCheckinWindow;
                                                location.LastCheckIn = endOfCheckinWindow;
                                                group.LastCheckIn = endOfCheckinWindow;
                                                groupType.LastCheckIn = endOfCheckinWindow;
                                                previousAttender.LastCheckIn = endOfCheckinWindow;
                                            }

                                            // finished finding assignment, verify everything is selected
                                            schedule.Selected = true;
                                            schedule.PreSelected = true;
                                            location.Selected = true;
                                            location.PreSelected = true;
                                            group.Selected = true;
                                            group.PreSelected = true;
                                            groupType.Selected = true;
                                            groupType.PreSelected = true;
                                            groupType.Selected = true;
                                            groupType.PreSelected = true;
                                            previousAttender.Selected = true;
                                            previousAttender.PreSelected = true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return true;
        }
    }
}