import { BrowserRouter, Navigate, NavLink, Route, Routes } from "react-router-dom";
import FullCalendarPage from "./FullCalendarPage";
import CustomGridPage from "./CustomGridPage";

export default function App() {
  return (
    <BrowserRouter>
      <header className="topnav">
        <span className="brand">Spike A — drag calendar</span>
        <nav aria-label="Calendar variants">
          <NavLink to="/fullcalendar">FullCalendar</NavLink>
          <NavLink to="/customgrid">Custom grid</NavLink>
        </nav>
      </header>
      <main>
        <Routes>
          <Route path="/" element={<Navigate to="/fullcalendar" replace />} />
          <Route path="/fullcalendar" element={<FullCalendarPage />} />
          <Route path="/customgrid" element={<CustomGridPage />} />
        </Routes>
      </main>
    </BrowserRouter>
  );
}
