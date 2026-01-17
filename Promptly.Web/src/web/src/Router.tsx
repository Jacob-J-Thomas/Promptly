import React from 'react';
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { ProtectedRoute } from './components/ProtectedRoute';
import { Login } from './pages/Login';
import { Register } from './pages/Register';
import { ProjectsList } from './pages/ProjectsList';
import { ProjectDetail } from './pages/ProjectDetail';
import { EnvironmentDetail } from './pages/EnvironmentDetail';
import { EnvironmentForm } from './pages/EnvironmentForm';
import { SuiteDetail } from './pages/SuiteDetail';
import { SuiteForm } from './pages/SuiteForm';
import { RunDetail } from './pages/RunDetail';
import { RunsList } from './pages/RunsList';

export const Router: React.FC = () => {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/login" element={<Login />} />
        <Route path="/register" element={<Register />} />

        <Route
          path="/"
          element={
            <ProtectedRoute>
              <ProjectsList />
            </ProtectedRoute>
          }
        />

        <Route
          path="/projects/:projectId"
          element={
            <ProtectedRoute>
              <ProjectDetail />
            </ProtectedRoute>
          }
        />

        <Route
          path="/projects/:projectId/environments/new"
          element={
            <ProtectedRoute>
              <EnvironmentForm />
            </ProtectedRoute>
          }
        />

        <Route
          path="/projects/:projectId/suites/new"
          element={
            <ProtectedRoute>
              <SuiteForm />
            </ProtectedRoute>
          }
        />

        <Route
          path="/environments/:environmentId"
          element={
            <ProtectedRoute>
              <EnvironmentDetail />
            </ProtectedRoute>
          }
        />

        <Route
          path="/suites/:suiteId"
          element={
            <ProtectedRoute>
              <SuiteDetail />
            </ProtectedRoute>
          }
        />

        <Route
          path="/suites/:suiteId/runs"
          element={
            <ProtectedRoute>
              <RunsList />
            </ProtectedRoute>
          }
        />

        <Route
          path="/runs/:runId"
          element={
            <ProtectedRoute>
              <RunDetail />
            </ProtectedRoute>
          }
        />

        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </BrowserRouter>
  );
};
