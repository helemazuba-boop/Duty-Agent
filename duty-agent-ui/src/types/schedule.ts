export interface ScheduleEntry {
  date: string;
  day?: string;
  note?: string;
  area_assignments: Record<string, string[]>;
}

export interface SchedulePlan {
  id: string;
  name: string;
  description?: string;
  rules?: string;
  created_at?: string;
}
